using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging.Abstractions;
using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// WebhookReceiver の統合テスト: レビューが却下し続けても試行は MaxFixAttempts で必ず止まり、無限ループしない。
public class WebhookReviewLoopTests
{
    // build/test を必ず成功にするフェイク（dotnet を起動しない）。
    private sealed class AlwaysVerifies : IBuildVerifier
    {
        public Task<Result<Unit, BuildError>> VerifyAsync(string filePath, string fixedFileContent, string? testCode = null)
            => Task.FromResult(Result.Success<Unit, BuildError>(Unit.Value));
    }

    // 検証できない環境（dotnet SDK が無い等）を模すフェイク。
    private sealed class AlwaysUnavailable : IBuildVerifier
    {
        public Task<Result<Unit, BuildError>> VerifyAsync(string filePath, string fixedFileContent, string? testCode = null)
            => Task.FromResult(Result.Failure<Unit, BuildError>(new VerifierUnavailable("no dotnet SDK")));
    }

    // 常にマーカー付きの修正を返すフェイク AI。
    private sealed class FakeAiCore : IAiCore
    {
        private const string Fixed = """
            // [AGENT-MANAGED-START: FetchStatusAsync]
            public async Task<TrainStatus> FetchStatusAsync() { await Task.Yield(); return null!; }
            // [AGENT-MANAGED-END: FetchStatusAsync]
            """;
        public Task<Result<AiOutput, AiError>> GenerateFixAsync(
            ErrorPayload payload, string? previousAttemptErrors = null, ErrorClass errorClass = ErrorClass.SchemaDrift)
            => Task.FromResult(Result.Success<AiOutput, AiError>(
                new AiOutput("desc", "TargetApp/TransitService.cs", Fixed, 90, "impact", "")));
    }

    // 常に却下するフェイクのレビュアー。
    private sealed class AlwaysRejects : IReviewer
    {
        public Task<ReviewVerdict> ReviewAsync(ErrorPayload payload, AiOutput output)
            => Task.FromResult(ReviewVerdict.Reject("却下", new[] { "テストがトートロジー" }));
    }

    // PR 作成までは到達しない想定だが、念のため成功を返すフェイク。
    private sealed class FakeGit : IGitOperator
    {
        public Task<Result<string, GitOperatorError>> CreatePrAsync(PrContext ctx)
            => Task.FromResult(Result.Success<string, GitOperatorError>("https://example/pr/1"));
    }

    [Fact]
    public async Task Review_rejecting_every_attempt_terminates_at_MaxFixAttempts()
    {
        const int maxAttempts = 2;
        var eventLog = new InMemoryEventLog();
        var receiver = new WebhookReceiver(
            ai: new FakeAiCore(),
            git: new FakeGit(),
            verifier: new AlwaysVerifies(),
            dedup: new InMemoryDedupGate(TimeSpan.FromMinutes(60)),
            triager: new ErrorTriager(new StubTriageClassifier()), // 業務データなので AI 修正対象になる
            reviewer: new AlwaysRejects(),
            concurrency: new SemaphoreConcurrencyGate(maxConcurrent: 4),
            eventLog: eventLog,
            maxFixAttempts: maxAttempts,
            logger: NullLogger<WebhookReceiver>.Instance);

        var payload = new ErrorPayload(
            ErrorType: "JsonException",
            ErrorMessage: "cannot map",
            StackTrace: "at X",
            RawJsonResponse: """{ "delays": { "value": 3 }, "line_id": "A" }""",
            ExpectedSchema: """{ "delay_minutes": 0 }""",
            FaultyMethodSource: "// [AGENT-MANAGED-START: FetchStatusAsync]\npublic void F(){}\n// [AGENT-MANAGED-END: FetchStatusAsync]",
            FilePath: "TargetApp/TransitService.cs");

        await receiver.ReceiveErrorAsync(payload);

        var events = await eventLog.GetRecentAsync(200);
        // 却下が続いても試行はちょうど MaxFixAttempts 回で止まる（無限ループしない）。
        Assert.Equal(maxAttempts, events.Count(e => e.Type == EventType.FixAttemptStarted));
        Assert.Equal(maxAttempts, events.Count(e => e.Type == EventType.ReviewRejected));
        // 承認も PR も無く、最終的に失敗で終わる。
        Assert.DoesNotContain(events, e => e.Type == EventType.ReviewApproved);
        Assert.DoesNotContain(events, e => e.Type == EventType.PrCreated);
        Assert.Contains(events, e => e.Type == EventType.PrFailed);
    }

    [Fact]
    public async Task Review_runs_even_when_verifier_is_unavailable()
    {
        // build/test を実行できない環境でも、レビューは走って PR の手前で品質を確認する。
        var eventLog = new InMemoryEventLog();
        var receiver = new WebhookReceiver(
            ai: new FakeAiCore(),
            git: new FakeGit(),
            verifier: new AlwaysUnavailable(),           // 検証できない
            dedup: new InMemoryDedupGate(TimeSpan.FromMinutes(60)),
            triager: new ErrorTriager(new StubTriageClassifier()),
            reviewer: new StubReviewer(),                // 常に承認
            concurrency: new SemaphoreConcurrencyGate(maxConcurrent: 4),
            eventLog: eventLog,
            maxFixAttempts: 2,
            logger: NullLogger<WebhookReceiver>.Instance);

        var payload = new ErrorPayload(
            "JsonException", "cannot map", "at X",
            """{ "delays": { "value": 3 }, "line_id": "A" }""",
            """{ "delay_minutes": 0 }""",
            "// [AGENT-MANAGED-START: FetchStatusAsync]\npublic void F(){}\n// [AGENT-MANAGED-END: FetchStatusAsync]",
            "TargetApp/TransitService.cs");

        await receiver.ReceiveErrorAsync(payload);

        var events = await eventLog.GetRecentAsync(200);
        // 検証できなくてもレビューが走り、承認されて PR 作成へ進む（失敗で終わらない）。
        // ここでは「レビュー承認まで到達し、失敗していない」ことで PR 作成への到達を確認する。
        Assert.Contains(events, e => e.Type == EventType.ReviewStarted);
        Assert.Contains(events, e => e.Type == EventType.ReviewApproved);
        Assert.DoesNotContain(events, e => e.Type == EventType.PrFailed);
        Assert.Equal(1, events.Count(e => e.Type == EventType.FixAttemptStarted)); // 1回目で承認＝再試行しない
    }
}
