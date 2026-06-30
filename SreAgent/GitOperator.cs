using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Options;
using Octokit;

namespace SreAgent;

// GitHub API 経由で「ブランチ作成 → ファイル更新 → PR 作成」を行う本番実装。
// 検証済みの修正（AiOutput）を受け取り、PR を作って URL を返すことに責務を絞っている。
public class GitOperator : IGitOperator
{
    private readonly IGitHubClient _github;
    private readonly GitHubSettings _settings;
    private readonly ILogger<GitOperator> _logger;

    public GitOperator(
        IOptions<GitHubSettings> settings,
        ILogger<GitOperator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        var client = new GitHubClient(new ProductHeaderValue("self-healing-sre-agent"))
        {
            Credentials = new Credentials(_settings.PersonalAccessToken)
        };
        _github = client;
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

        try
        {
            // 2. 元ファイルを取得する。
            var owner = _settings.RepoOwner;
            var name = _settings.RepoName;
            IReadOnlyList<RepositoryContent> contents;
            try
            {
                contents = await _github.Repository.Content.GetAllContentsByRef(
                    owner, name, output.FilePath, _settings.BaseBranch);
            }
            catch (NotFoundException)
            {
                return Result.Failure<string, GitOperatorError>(new FileNotFoundError(output.FilePath));
            }
            var current = contents[0];

            // 3. マーカー範囲を修正コードで置き換える。
            string replaced;
            try
            {
                replaced = ReplaceMethod(current.Content, methodName, output.FixedMethodCode);
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<string, GitOperatorError>(new MarkerMissingInOutputError(ex.Message));
            }

            // 4. ブランチを作成する。ブランチ名はエラー署名から決まる。
            var branchName = GenerateBranchName(methodName, ctx.Signature);
            var baseRef = await _github.Git.Reference.Get(owner, name, $"heads/{_settings.BaseBranch}");
            
            // コミット更新対象ファイルのSHA。初期値は base ブランチのファイル SHA
            string targetFileSha = current.Sha;
            
            try
            {
                await _github.Git.Reference.Create(owner, name,
                    new NewReference($"refs/heads/{branchName}", baseRef.Object.Sha));
                _logger.LogInformation("Created new branch '{BranchName}' from base branch SHA '{Sha}'", branchName, baseRef.Object.Sha);
            }
            catch (ApiValidationException ex) when (IsAlreadyExists(ex))
            {
                // すでに同名ブランチがある場合：
                // ベースへのリセット（force: true）は既存PRの自動クローズ（差分0化によるGitHub仕様）を引き起こすため行わない。
                // 単に既存ブランチ上のファイルの最新 SHA を再取得して、競合を防ぎつつ上書きコミットできるようにする。
                _logger.LogInformation("Branch '{BranchName}' already exists. Reusing it and retrieving target file SHA...", branchName);
                try
                {
                    var branchContents = await _github.Repository.Content.GetAllContentsByRef(
                        owner, name, output.FilePath, branchName);
                    if (branchContents.Count > 0)
                    {
                        targetFileSha = branchContents[0].Sha;
                    }
                }
                catch (NotFoundException)
                {
                    // 万が一ブランチ上にファイルがない場合は main ブランチの SHA をそのまま利用
                }
            }

            // 5. 変更をコミットする。
            await _github.Repository.Content.UpdateFile(owner, name, output.FilePath,
                new UpdateFileRequest(
                    message: $"auto-fix: {methodName} for new API schema",
                    content: replaced,
                    sha: targetFileSha, // 強制更新されたブランチ上の SHA を設定
                    branch: branchName));

            // 6. PR を作成する。すでに PR がある場合は再利用（フォールバック）する。
            PullRequest pr;
            var prBody = BuildPrBody(ctx);
            try
            {
                pr = await _github.PullRequest.Create(owner, name,
                    new NewPullRequest(
                        title: $"auto-fix: {methodName}",
                        head: branchName,
                        baseRef: _settings.BaseBranch)
                    {
                        Body = prBody
                    });
                _logger.LogInformation("Created PR #{Number}: {Url}", pr.Number, pr.HtmlUrl);
            }
            catch (ApiValidationException ex) when (IsPrAlreadyExists(ex))
            {
                _logger.LogInformation("Pull Request for branch '{BranchName}' already exists. Retrieving existing PR...", branchName);
                
                // 既存のオープンなPRを取得
                var prs = await _github.PullRequest.GetAllForRepository(owner, name, new PullRequestRequest
                {
                    Head = $"{owner}:{branchName}",
                    State = ItemStateFilter.Open
                });

                if (prs.Count > 0)
                {
                    pr = prs[0];
                    _logger.LogInformation("Reusing existing PR #{Number}: {Url}", pr.Number, pr.HtmlUrl);
                    // PRの本文を最新情報で上書き更新
                    try
                    {
                        await _github.PullRequest.Update(owner, name, pr.Number, new PullRequestUpdate { Body = prBody });
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogWarning(updateEx, "Failed to update body of existing PR #{Number}", pr.Number);
                    }
                }
                else
                {
                    throw;
                }
            }

            return Result.Success<string, GitOperatorError>(pr.HtmlUrl);
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "GitHub API call failed");
            return Result.Failure<string, GitOperatorError>(new GitHubApiError(ex.Message));
        }
    }

    // 「ブランチが既に存在する」エラーを判定する。
    private static bool IsAlreadyExists(ApiValidationException ex) =>
        ex.ApiError?.Errors?.Any(e => string.Equals(e.Code, "already_exists", StringComparison.OrdinalIgnoreCase)) == true
        || ContainsAlreadyExists(ex.ApiError?.Message)
        || ContainsAlreadyExists(ex.Message);

    private static bool ContainsAlreadyExists(string? s) =>
        s is not null && s.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    // 「PRが既に存在する」エラーを判定する。
    private static bool IsPrAlreadyExists(ApiValidationException ex) =>
        ex.ApiError?.Errors?.Any(e => e.Message != null && e.Message.Contains("A pull request already exists", StringComparison.OrdinalIgnoreCase)) == true
        || ex.Message?.Contains("A pull request already exists", StringComparison.OrdinalIgnoreCase) == true;
    // PR 本文を組み立てる。概要に加えて、レビュアー向けの検知方式・検証結果・スキーマ差分などを載せる。
    private static string BuildPrBody(PrContext ctx)
    {
        var output = ctx.Output;
        var detection = ctx.DetectionSource == DetectionSource.RealRequestPath
            ? "案A: 実リクエスト経路（実トラフィックが例外を踏んで自動起動）"
            : "案C: ログ駆動（Cloud Logging の JsonException → Pub/Sub → 自動起動）";
        var attemptLine = ctx.AttemptCount <= 1
            ? "1 回目の生成でビルド" + (ctx.TestsVerified ? "＆テスト" : "") + "に成功"
            : $"{ctx.AttemptCount} 回目の生成でビルド" + (ctx.TestsVerified ? "＆テスト" : "") + "に成功（自律リトライ）";
        var testLine = ctx.TestsVerified
            ? "✅ 生成テストを `dotnet test` で実行し合格"
            : "⚠️ テストは未実行（環境制約で検証スキップ、ビルドのみ確認）";
        var testCodeBlock = string.IsNullOrWhiteSpace(output.TestCode)
            ? "_（テストコードなし）_"
            : $"```csharp\n{Truncate(output.TestCode, 2000)}\n```";

        return $"""
            ## 概要
            {output.PrDescription}

            ## このPRが自動生成された経緯
            このPRは SRE Agent が**本番のエラーを起点に**自律生成しました（人がIDEで依頼したものではありません）。

            ## 検知方式
            {detection}

            ## エラーシグネチャ（重複抑制キー）
            `{ctx.Signature}`
            同一スキーマ drift はこのシグネチャで畳み込まれ、決定論ブランチ名により重複PRを作りません。

            ## 修正試行
            {attemptLine}

            ## ビルド・テスト検証
            - ✅ `dotnet build` 成功（コンパイル可能を確認）
            - {testLine}

            ## AI 信頼度
            **{output.Confidence} / 100**

            ## 影響範囲（AI 自動推定）
            {output.ImpactAnalysis}

            ## スキーマ差分
            <details><summary>旧スキーマ（Before / 期待）</summary>

            ```
            {Truncate(ctx.SchemaBefore, 1500)}
            ```
            </details>

            <details><summary>新スキーマ（After / 実際に受信した生JSON）</summary>

            ```json
            {Truncate(ctx.SchemaAfter, 1500)}
            ```
            </details>

            ## 生成テスト
            <details><summary>AI が生成したテストコード</summary>

            {testCodeBlock}
            </details>

            ---
            🤖 このPRは SRE Agent（自律追従型データ連携エージェント）が自動作成しました。
            """;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n...(truncated)";

    // 修正コード中の [AGENT-MANAGED-START: メソッド名] マーカーからメソッド名を取り出す。無ければ null。
    public static string? ExtractMethodName(string fixedMethodCode)
    {
        var match = Regex.Match(fixedMethodCode, @"\[AGENT-MANAGED-START:\s*(\w+)\s*\]");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ファイル内の START/END マーカーで囲まれた範囲を newCode に置き換える。マーカー外は変えない。
    // マーカーが見つからなければ InvalidOperationException を投げる。
    public static string ReplaceMethod(string fileContent, string methodName, string newCode)
    {
        var start = $"// [AGENT-MANAGED-START: {methodName}]";
        var end = $"// [AGENT-MANAGED-END: {methodName}]";
        var startIndex = fileContent.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            throw new InvalidOperationException($"Marker start not found for method: {methodName}");
        }
        var endIndex = fileContent.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidOperationException($"Marker end not found for method: {methodName}");
        }
        return fileContent[..startIndex] + newCode + fileContent[(endIndex + end.Length)..];
    }

    // エラー署名からブランチ名を決める。同じメソッド・同じ署名なら必ず同じ名前になる（重複 PR 防止）。
    public static string GenerateBranchName(string methodName, string signature)
        => $"auto-fix/{methodName}-{ShortHash(signature)}";

    private static string ShortHash(string s)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];
}
