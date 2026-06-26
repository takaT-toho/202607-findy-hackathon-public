namespace SreAgent;

// エージェントの処理中に起きる出来事の種類。ダッシュボードのタイムラインに表示する。
public enum EventType
{
    WebhookReceived,
    DuplicateSuppressed,    // 同じエラーを重複として処理せずスキップした
    Triaged,                // エラーを分類した
    TriageRejected,         // 一過性障害と判断して棄却した
    Escalated,              // 人手が必要と判断した
    FixAttemptStarted,      // 修正の 1 試行を開始した
    FixAttemptFailed,
    AiCallStarted,
    AiCallCompleted,
    AiCallFailed,
    BuildVerifyStarted,
    BuildVerifyCompleted,
    BuildVerifyFailed,
    TestVerifyStarted,      // 生成テストの dotnet test 実行
    TestVerifyCompleted,
    TestVerifyFailed,
    ReviewStarted,          // 独立レビュー
    ReviewApproved,
    ReviewRejected,
    PrCreationStarted,
    PrCreated,
    PrFailed
}

// 1 件のイベント。CaseId は同じエラーケースのイベントを束ねる相関 ID。
// ケースに紐づかないイベント（起動ログ等）では null。
public record AgentEvent(
    DateTimeOffset Timestamp,
    EventType Type,
    string Summary,
    string? Detail,
    string? CaseId = null
);
