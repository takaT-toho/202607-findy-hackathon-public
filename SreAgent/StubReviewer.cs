namespace SreAgent;

// 開発環境用のレビュアー。LLM を呼ばず常に承認する（テストを非決定的にしないため）。
public class StubReviewer : IReviewer
{
    public Task<ReviewVerdict> ReviewAsync(ErrorPayload payload, AiOutput output) =>
        Task.FromResult(ReviewVerdict.Approve("[STUB] build/test 通過済みのため承認（dev は LLM レビューを行わない）。"));
}
