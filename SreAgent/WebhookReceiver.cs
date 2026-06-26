using CSharpFunctionalExtensions;
using IResult = Microsoft.AspNetCore.Http.IResult;

namespace SreAgent;

// エラー通知を受けて修正までの一連の流れを取りまとめる中心クラス。
// 流れ: 重複抑制 → Triage → [AI生成 → ビルド/テスト検証 → レビュー、失敗なら再試行]×N → PR作成。
public class WebhookReceiver
{
    private readonly IAiCore _ai;
    private readonly IGitOperator _git;
    private readonly IBuildVerifier _verifier;
    private readonly IDedupGate _dedup;
    private readonly IErrorTriager _triager;
    private readonly IReviewer _reviewer;
    private readonly IConcurrencyGate _concurrency;
    private readonly IEventLog _eventLog;
    private readonly int _maxFixAttempts;
    private readonly TimeSpan _acquireWait;
    private readonly ILogger<WebhookReceiver> _logger;

    public WebhookReceiver(
        IAiCore ai,
        IGitOperator git,
        IBuildVerifier verifier,
        IDedupGate dedup,
        IErrorTriager triager,
        IReviewer reviewer,
        IConcurrencyGate concurrency,
        IEventLog eventLog,
        int maxFixAttempts,
        ILogger<WebhookReceiver> logger,
        int acquireWaitSeconds = 10)
    {
        _ai = ai;
        _git = git;
        _verifier = verifier;
        _dedup = dedup;
        _triager = triager;
        _reviewer = reviewer;
        _concurrency = concurrency;
        _eventLog = eventLog;
        _maxFixAttempts = maxFixAttempts < 1 ? 1 : maxFixAttempts;
        _acquireWait = TimeSpan.FromSeconds(acquireWaitSeconds < 0 ? 0 : acquireWaitSeconds);
        _logger = logger;
    }

    // エラー通知 1 件を処理する入口。重複ならスキップ、AI で直せないなら棄却、直せるなら修正して PR を作る。
    public async Task<IResult> ReceiveErrorAsync(ErrorPayload payload, DetectionSource source = DetectionSource.RealRequestPath)
    {
        if (string.IsNullOrWhiteSpace(payload.RawJsonResponse) ||
            string.IsNullOrWhiteSpace(payload.FaultyMethodSource))
        {
            _logger.LogWarning("Webhook payload missing required fields");
            return Results.BadRequest(new { error = "Missing required payload fields" });
        }

        // このケースの ID（エラー署名）を先に決め、以降このメソッドが記録する全イベントへ自動で付ける。
        var signature = ErrorSignature.Compute(payload);
        using var caseScope = CaseContext.Begin(signature);

        await _eventLog.RecordAsync(EventType.WebhookReceived,
            $"Webhook received via {source}: {payload.ErrorType}",
            $"Message: {payload.ErrorMessage}");

        // 重複抑制: 同じエラーを短時間に処理済みなら、AI も PR も動かさず返す。
        if (!_dedup.TryAcquire(signature))
        {
            _logger.LogInformation("Duplicate drift suppressed: {Signature}", signature);
            await _eventLog.RecordAsync(EventType.DuplicateSuppressed,
                $"Duplicate suppressed: {signature}",
                "同一シグネチャを抑制窓内に処理済みのため、AI/PR をスキップしました。");
            return Results.Ok(new { deduplicated = true, signature });
        }

        // Triage: 分類して「AI で直すべきか」を判断する。一過性障害などはここで棄却し AI に渡さない。
        var verdict = await _triager.TriageAsync(payload);
        await _eventLog.RecordAsync(EventType.Triaged,
            $"Classified as {verdict.Class} (fixable={verdict.IsAiFixable}, conf {verdict.Confidence})",
            verdict.Reason);

        if (!verdict.IsAiFixable)
        {
            // 一過性なら棄却、それ以外は人手へエスカレーション。dedup は解放しない（窓内は再通知しない）。
            var evt = verdict.Class == ErrorClass.TransientUpstream
                ? EventType.TriageRejected : EventType.Escalated;
            await _eventLog.RecordAsync(evt, $"Not AI-fixable: {verdict.Class}", verdict.Reason);
            _logger.LogInformation("Triage rejected/escalated {Signature}: {Class}", signature, verdict.Class);
            return Results.Ok(new
            {
                signature,
                triaged = verdict.Class.ToString(),
                ai_fixable = false,
                escalated = true,
                reason = verdict.Reason
            });
        }

        // 同時実行の上限チェック。空きが無く待っても空かなければ過負荷としてエスカレーションする。
        if (!await _concurrency.TryAcquireAsync(_acquireWait))
        {
            await _eventLog.RecordAsync(EventType.Escalated,
                "Concurrency limit reached; shedding load",
                $"同時実行上限により {_acquireWait.TotalSeconds:0}s 待っても空きが無く、過負荷としてエスカレーションしました。");
            _logger.LogWarning("Concurrency limit reached for {Signature}; escalating", signature);
            return Results.Json(new { signature, escalated = true, reason = "concurrency limit reached" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var (output, attempts, testsVerified, failure) = await GenerateVerifiedFixAsync(payload, verdict.Class);
            if (output is null)
            {
                // どの試行でも検証を通せなかった、または AI 側のインフラ障害。
                _dedup.Release(signature); // 後で再挑戦できるようゲートを解放する。
                return failure ?? Results.StatusCode(500);
            }

            var ctx = new PrContext(
                Output: output,
                Signature: signature,
                DetectionSource: source,
                AttemptCount: attempts,
                TestsVerified: testsVerified,
                SchemaBefore: payload.ExpectedSchema,
                SchemaAfter: payload.RawJsonResponse);

            var prResult = await _git.CreatePrAsync(ctx);
            return prResult.Match(
                onSuccess: url => (IResult)Results.Ok(new
                {
                    pr_url = url,
                    signature,
                    attempts,
                    tests_verified = testsVerified,
                    confidence = output.Confidence,
                    impact_analysis = output.ImpactAnalysis
                }),
                onFailure: err =>
                {
                    // 同名ブランチが既にある = 同じエラーの PR が既に存在する（重複のバックストップ）。
                    if (err is BranchAlreadyExistsError)
                    {
                        return Results.Ok(new { deduplicated = true, signature, reason = "branch already exists" });
                    }
                    _dedup.Release(signature);
                    return Results.Problem(title: err.GetType().Name, detail: err.Detail, statusCode: 500);
                });
        }
        catch
        {
            _dedup.Release(signature);
            throw;
        }
        finally
        {
            // 確保したスロットは成功・失敗・例外いずれでも必ず返す。
            _concurrency.Release();
        }
    }

    // ビルド/テストとレビューを通る修正が得られるまで、最大 MaxFixAttempts 回 AI を呼ぶ。
    // 返り値: (検証済み出力 or null, 試行回数, テスト実行済みか, 早期失敗時の HTTP 応答)。
    private async Task<(AiOutput? output, int attempts, bool testsVerified, IResult? failure)> GenerateVerifiedFixAsync(ErrorPayload payload, ErrorClass errorClass)
    {
        string? previousErrors = null;

        for (var attempt = 1; attempt <= _maxFixAttempts; attempt++)
        {
            await _eventLog.RecordAsync(EventType.FixAttemptStarted,
                $"Fix attempt {attempt}/{_maxFixAttempts}",
                previousErrors is null ? null : $"Retrying with previous errors:\n{Truncate(previousErrors, 400)}");

            var aiResult = await _ai.GenerateFixAsync(payload, previousErrors, errorClass);
            if (aiResult.IsFailure)
            {
                // 通信・クォータの障害は再試行しても無駄なので即終了。
                // 出力の不備（スキーマ・マーカー）はエラーを次プロンプトに渡して再試行する。
                switch (aiResult.Error)
                {
                    case ApiQuotaExceededError:
                        return (null, attempt, false, Results.StatusCode(503));
                    case NetworkError:
                        return (null, attempt, false, Results.StatusCode(502));
                    default:
                        previousErrors = aiResult.Error.Detail;
                        await _eventLog.RecordAsync(EventType.FixAttemptFailed,
                            $"Attempt {attempt} failed: {aiResult.Error.GetType().Name}", aiResult.Error.Detail);
                        continue;
                }
            }

            // AI が推測した file_path は信用せず、通知元が伝えてきた正しいパスで上書きする。
            var corrected = aiResult.Value with { FilePath = payload.FilePath };

            var verify = await VerifyAttemptAsync(corrected);
            var verified = verify.IsSuccess;
            // verify.Error は失敗時しか触れない（成功時に触ると例外）。
            var verifierUnavailable = verify.IsFailure && verify.Error is VerifierUnavailable;

            // ビルド/テストが「失敗」したらレビューに進まず、エラーを渡して再試行する。
            if (!verified && !verifierUnavailable)
            {
                var evt = verify.Error is TestFailed ? EventType.TestVerifyFailed : EventType.BuildVerifyFailed;
                await _eventLog.RecordAsync(evt,
                    $"Attempt {attempt} failed: {verify.Error.GetType().Name}", verify.Error.Detail);
                previousErrors = verify.Error.Detail;
                continue;
            }

            // 「成功」または「環境的に検証できない（dotnet SDK が無い等）」場合はレビューへ進む。
            // レビューはテキストを見るだけでビルド環境を必要としないので、検証不能でも品質ゲートとして必ず通す。
            var testsRun = verified && !string.IsNullOrWhiteSpace(corrected.TestCode);
            if (verified)
            {
                await _eventLog.RecordAsync(EventType.TestVerifyCompleted,
                    $"Attempt {attempt} verified (build{(testsRun ? "+test" : "")} OK)", null);
            }
            else
            {
                _logger.LogWarning("Verifier unavailable: {Detail}. Reviewing without local build/test.", verify.Error.Detail);
                await _eventLog.RecordAsync(EventType.BuildVerifyFailed,
                    $"Attempt {attempt}: verifier unavailable (soft-fail; レビューは実施)", verify.Error.Detail);
            }

            // 独立レビュー。却下なら指摘を次プロンプトに渡して再試行する（試行回数で必ず打ち切られる）。
            // 承認されて初めて PR 作成へ進む。
            await _eventLog.RecordAsync(EventType.ReviewStarted, $"Reviewing attempt {attempt}", null);
            var review = await _reviewer.ReviewAsync(payload, corrected);
            if (review.Approved)
            {
                await _eventLog.RecordAsync(EventType.ReviewApproved,
                    $"Attempt {attempt} approved", review.Reason);
                return (corrected, attempt, testsRun, null);
            }

            var rejectionDetail = FormatReview(review);
            await _eventLog.RecordAsync(EventType.ReviewRejected,
                $"Attempt {attempt} rejected by review", rejectionDetail);
            previousErrors = rejectionDetail;
        }

        // 上限まで通らなかった。
        await _eventLog.RecordAsync(EventType.PrFailed,
            $"Gave up after {_maxFixAttempts} attempts (build/test never passed)", Truncate(previousErrors ?? "", 800));
        return (null, _maxFixAttempts, false,
            Results.Problem(
                title: "FixVerificationExhausted",
                detail: $"{_maxFixAttempts} 回の試行でビルド/テストを通せませんでした。最後のエラー: {Truncate(previousErrors ?? "", 500)}",
                statusCode: 422));
    }

    // 修正案を実際のソースに当ててビルド/テスト検証する。
    // ローカルにソースが無い環境では VerifierUnavailable（検証スキップ扱い）を返す。
    private async Task<Result<Unit, BuildError>> VerifyAttemptAsync(AiOutput output)
    {
        var methodName = GitOperator.ExtractMethodName(output.FixedMethodCode);
        if (methodName is null)
        {
            // マーカー欠落は AI 側で直せる不備なので、再試行対象にする。
            return Result.Failure<Unit, BuildError>(
                new CompilationFailed("fixed_method_code に [AGENT-MANAGED-START: X] / [...-END: X] マーカーがありません"));
        }

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            return Result.Failure<Unit, BuildError>(new VerifierUnavailable("Repository root (.git) not found"));
        }

        var targetPath = Path.Combine(repoRoot, output.FilePath);
        if (!File.Exists(targetPath))
        {
            return Result.Failure<Unit, BuildError>(new VerifierUnavailable($"Local source not found: {output.FilePath}"));
        }

        var original = await File.ReadAllTextAsync(targetPath);
        string replaced;
        try
        {
            replaced = GitOperator.ReplaceMethod(original, methodName, output.FixedMethodCode);
        }
        catch (InvalidOperationException ex)
        {
            // 元ファイルにマーカーが無い等。AI 側で直せる問題として再試行対象にする。
            return Result.Failure<Unit, BuildError>(new CompilationFailed(ex.Message));
        }

        await _eventLog.RecordAsync(EventType.TestVerifyStarted,
            $"Verifying build/test for {output.FilePath}", null);
        return await _verifier.VerifyAsync(output.FilePath, replaced, output.TestCode);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...(truncated)";

    // レビュー却下の理由と指摘を、次プロンプトに渡す「直してほしい点」の文章に整える。
    private static string FormatReview(ReviewVerdict review)
    {
        var findings = review.Findings.Count == 0
            ? string.Empty
            : "\n指摘:\n- " + string.Join("\n- ", review.Findings);
        return $"レビューで却下されました: {review.Reason}{findings}";
    }
}
