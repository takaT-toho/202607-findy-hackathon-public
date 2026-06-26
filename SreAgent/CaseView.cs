namespace SreAgent;

// 1 件のエラーケースが今どの段階にあるかを表す状態。
public enum CaseState
{
    Detected,   // Webhook 受信のみ
    Triaged,    // 分類済み（まだ修正に入っていない）
    Rejected,   // 一過性として棄却（AI/PR を起動せず終了）
    Escalated,  // 人手が必要と判断（AI/PR を起動せず終了）
    Fixing,     // 修正中（AI 生成 → build/test）
    Verified,   // build/test 通過（レビュー待ち）
    Reviewing,  // レビュー中（却下なら Fixing へ戻る）
    PrOpened,   // PR 作成完了（成功終端）
    Failed      // 試行上限到達・PR 作成失敗（失敗終端）
}

// ダッシュボードに表示する 1 ケース分のまとまった情報。
public record CaseView(
    string CaseId,
    ErrorClass? Class,
    CaseState State,
    int FixAttempts,
    int? TriageConfidence,
    string? PrUrl,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastUpdated,
    IReadOnlyList<AgentEvent> Timeline);
