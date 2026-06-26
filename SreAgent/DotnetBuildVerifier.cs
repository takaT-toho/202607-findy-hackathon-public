using System.Diagnostics;
using System.Text;
using CSharpFunctionalExtensions;

namespace SreAgent;

// 実際に dotnet build / dotnet test を走らせて修正案を検証する IBuildVerifier 実装。
// 元のソースは触らず、一時ディレクトリにコピーしてから修正版を当てて検証し、最後に必ず後片付けする。
// 流れ: プロジェクト特定 → 一時コピー → 修正版で上書き → build →（testCode があれば）test。
// dotnet CLI やソースが無い、NuGet 復元できない等の環境制約のときは VerifierUnavailable を返し、
// 呼び出し元は検証スキップとして扱う（環境の都合で良い修正を止めない）。
public class DotnetBuildVerifier : IBuildVerifier
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(180);
    private static readonly string[] ExcludeDirs = { "bin", "obj", ".vs", "node_modules", "stub-output" };

    private readonly ILogger<DotnetBuildVerifier> _logger;

    public DotnetBuildVerifier(ILogger<DotnetBuildVerifier> logger)
    {
        _logger = logger;
    }

    // filePath（リポジトリ相対）を fixedFileContent で置き換えた状態でビルドする。
    // testCode があればビルド後にテストも実行する。
    public async Task<Result<Unit, BuildError>> VerifyAsync(string filePath, string fixedFileContent, string? testCode = null)
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return Result.Failure<Unit, BuildError>(
                new VerifierUnavailable("Repository root (.git) not found from AppContext.BaseDirectory"));
        }

        var sourceFilePath = Path.Combine(repoRoot, filePath);
        if (!File.Exists(sourceFilePath))
        {
            return Result.Failure<Unit, BuildError>(
                new VerifierUnavailable($"Source file does not exist: {filePath}"));
        }

        var projectDir = FindProjectDir(sourceFilePath);
        if (projectDir is null)
        {
            return Result.Failure<Unit, BuildError>(
                new VerifierUnavailable($"No .csproj found upwards from {filePath}"));
        }

        if (!await IsDotnetAvailableAsync())
        {
            return Result.Failure<Unit, BuildError>(
                new VerifierUnavailable("dotnet CLI not on PATH; cannot run build verification"));
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"sre-verify-{Guid.NewGuid():N}");
        var targetCopyDir = Path.Combine(tempRoot, "target");
        try
        {
            CopyDirectory(projectDir, targetCopyDir);

            // コピー先の対象ファイルを修正版で上書きする。
            var relativeToProject = Path.GetRelativePath(projectDir, sourceFilePath);
            var destFile = Path.Combine(targetCopyDir, relativeToProject);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            await File.WriteAllTextAsync(destFile, fixedFileContent);

            // まずビルドして、コンパイルできるかを確認する。
            _logger.LogInformation("Running dotnet build in {Temp}", targetCopyDir);
            var (buildExit, buildOutput) = await RunDotnetAsync("build --nologo -c Debug -v quiet", targetCopyDir, BuildTimeout);
            if (buildExit != 0)
            {
                var summary = ExtractErrors(buildOutput);
                _logger.LogWarning("Build failed (exit {Code}):\n{Errors}", buildExit, summary);
                return Result.Failure<Unit, BuildError>(new CompilationFailed(summary));
            }

            // テストコードが無ければビルド成功で完了。
            if (string.IsNullOrWhiteSpace(testCode))
            {
                return Result.Success<Unit, BuildError>(Unit.Value);
            }

            return await RunGeneratedTestsAsync(tempRoot, targetCopyDir, projectDir, testCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build verification threw unexpectedly");
            return Result.Failure<Unit, BuildError>(new VerifierUnavailable($"Unexpected error: {ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp dir: {Dir}", tempRoot);
            }
        }
    }

    // 生成テストを一時的なテストプロジェクトに置いて dotnet test を実行する。
    // 合格なら成功、不合格なら TestFailed、環境的に実行できないなら VerifierUnavailable。
    private async Task<Result<Unit, BuildError>> RunGeneratedTestsAsync(
        string tempRoot, string targetCopyDir, string originalProjectDir, string testCode)
    {
        var targetCsproj = Directory.GetFiles(targetCopyDir, "*.csproj").FirstOrDefault();
        if (targetCsproj is null)
        {
            return Result.Failure<Unit, BuildError>(new VerifierUnavailable("Copied target project has no .csproj"));
        }

        var testProjDir = Path.Combine(tempRoot, "tests");
        Directory.CreateDirectory(testProjDir);

        var csprojRelative = Path.GetRelativePath(testProjDir, targetCsproj).Replace('\\', '/');
        var testCsproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{csprojRelative}" />
              </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(Path.Combine(testProjDir, "SreVerifyTests.csproj"), testCsproj);
        await File.WriteAllTextAsync(Path.Combine(testProjDir, "GeneratedTests.cs"), testCode);

        _logger.LogInformation("Running dotnet test in {Temp}", testProjDir);
        var (testExit, testOutput) = await RunDotnetAsync("test --nologo -v quiet", testProjDir, TestTimeout);

        if (testExit == 0)
        {
            return Result.Success<Unit, BuildError>(Unit.Value);
        }

        // NuGet 復元失敗・SDK 不足などは「テストを実行できない」環境制約として検証スキップ扱いにする。
        if (IsRestoreOrEnvironmentFailure(testOutput))
        {
            _logger.LogWarning("Test harness unavailable (restore/env). Soft-failing.\n{Out}", ExtractErrors(testOutput));
            return Result.Failure<Unit, BuildError>(
                new VerifierUnavailable($"dotnet test could not run (restore/env): {ExtractErrors(testOutput)}"));
        }

        var summary = ExtractTestFailureSummary(testOutput);
        _logger.LogWarning("Tests failed (exit {Code}):\n{Summary}", testExit, summary);
        return Result.Failure<Unit, BuildError>(new TestFailed(summary));
    }

    // .git のあるリポジトリルートを上方向に探す。
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    // 対象ファイルから上方向に .csproj のあるディレクトリ（プロジェクトの場所）を探す。
    private static string? FindProjectDir(string filePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (dir is not null && !Directory.GetFiles(dir.FullName, "*.csproj").Any())
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    // dotnet CLI が使えるかを確認する。
    private static async Task<bool> IsDotnetAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ディレクトリを再帰コピーする。bin/obj など不要なフォルダは除外して軽くする。
    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (ExcludeDirs.Contains(name)) continue;
            CopyDirectory(dir, Path.Combine(dest, name));
        }
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }
    }

    // dotnet コマンドを実行し、終了コードと出力（標準出力＋標準エラー）を返す。タイムアウトしたら強制終了する。
    private async Task<(int exitCode, string output)> RunDotnetAsync(string args, string workingDir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 出力の文字化けを避けるため、dotnet のメッセージ言語を英語に固定する。
            Environment = { ["DOTNET_CLI_UI_LANGUAGE"] = "en" },
        };

        var sb = new StringBuilder();
        using var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (124, $"dotnet {args} timed out after {timeout.TotalSeconds}s");
        }

        return (process.ExitCode, sb.ToString());
    }

    // ビルド出力からエラー行だけを最大 10 件抜き出す（ログを読みやすくするため）。
    private static string ExtractErrors(string buildOutput)
    {
        var errorLines = buildOutput.Split('\n')
            .Where(line => line.Contains(": error", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        return errorLines.Count > 0
            ? string.Join("\n", errorLines)
            : buildOutput.Length > 2000
                ? buildOutput[..2000] + "...(truncated)"
                : buildOutput;
    }

    // テスト不合格ではなく「そもそも環境的に実行できなかった」サイン（NuGet 復元失敗等）かを判定する。
    private static bool IsRestoreOrEnvironmentFailure(string output)
    {
        return output.Contains(": error NU", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Unable to load the service index", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Unable to find package", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Could not resolve SDK", StringComparison.OrdinalIgnoreCase);
    }

    // テスト失敗の要約を抜き出す。AI が次の試行で直せるよう、エラー行や Assert 行を返す。
    private static string ExtractTestFailureSummary(string testOutput)
    {
        var lines = testOutput.Split('\n')
            .Where(line =>
                line.Contains(": error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Failed!", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Assert.", StringComparison.OrdinalIgnoreCase)
                || line.TrimStart().StartsWith("Failed ", StringComparison.OrdinalIgnoreCase))
            .Take(15)
            .ToList();
        return lines.Count > 0
            ? string.Join("\n", lines)
            : testOutput.Length > 2000 ? testOutput[..2000] + "...(truncated)" : testOutput;
    }
}
