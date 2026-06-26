using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace SreAgent;

// 開発環境用の Git 操作。GitHub には通信せず、修正結果をローカルの ./stub-output/ に書き出すだけ。
// GitHub を汚さず・PAT なしで「修正コードが実ファイルに正しく適用されるか」を確認できる。
public class StubGitOperator : IGitOperator
{
    private readonly ILogger<StubGitOperator> _logger;

    public StubGitOperator(ILogger<StubGitOperator> logger)
    {
        _logger = logger;
    }

    public async Task<Result<string, GitOperatorError>> CreatePrAsync(PrContext ctx)
    {
        var output = ctx.Output;

        // 1. 修正コードから対象メソッド名（マーカー）を取り出す。
        var methodName = ExtractMethodName(output.FixedMethodCode);
        if (methodName is null)
        {
            return Result.Failure<string, GitOperatorError>(
                new MarkerMissingInOutputError("fixed_method_code does not contain [AGENT-MANAGED-START: ...] marker"));
        }

        // 2. 対象ファイルを読み込む。
        var repoRoot = FindRepoRoot();
        var targetPath = Path.Combine(repoRoot, output.FilePath);
        if (!File.Exists(targetPath))
        {
            return Result.Failure<string, GitOperatorError>(new FileNotFoundError(output.FilePath));
        }
        var original = await File.ReadAllTextAsync(targetPath);

        // 3. マーカー範囲を修正コードで置き換える。
        string replaced;
        try
        {
            replaced = GitOperator.ReplaceMethod(original, methodName, output.FixedMethodCode);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<string, GitOperatorError>(new MarkerMissingInOutputError(ex.Message));
        }

        // 4. ./stub-output/{ブランチ名}/{ファイルパス} に書き出す。
        //    ブランチ名はエラー署名から決まるので、同じエラーなら同じ出力先になる。
        var branchName = GitOperator.GenerateBranchName(methodName, ctx.Signature);
        var outDir = Path.Combine(repoRoot, "stub-output", branchName);
        var outPath = Path.Combine(outDir, output.FilePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            await File.WriteAllTextAsync(outPath, replaced);
        }
        catch (IOException ex)
        {
            return Result.Failure<string, GitOperatorError>(new LocalIoError(ex.Message));
        }

        _logger.LogInformation(
            "[STUB] Would create PR: {Description} (confidence={Confidence}, file=file:///{Path})",
            output.PrDescription, output.Confidence, outPath.Replace('\\', '/'));

        return Result.Success<string, GitOperatorError>($"file:///{outPath.Replace('\\', '/')}");
    }

    private static string? ExtractMethodName(string fixedMethodCode)
    {
        var match = Regex.Match(fixedMethodCode, @"\[AGENT-MANAGED-START:\s*(\w+)\s*\]");
        return match.Success ? match.Groups[1].Value : null;
    }

    // 実行ディレクトリから親をたどって .git のあるリポジトリルートを探す。
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
