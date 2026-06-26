namespace SreAgent;

// ビルド・テストを通過した修正案を、生成元とは別の視点で最終レビューする。
// 開発環境では StubReviewer（常に承認）、本番では LLM を使う Reviewer が使われる。
public interface IReviewer
{
    // Approve なら PR 作成へ進み、Reject なら指摘を添えて修正ループへ差し戻す。
    Task<ReviewVerdict> ReviewAsync(ErrorPayload payload, AiOutput output);
}
