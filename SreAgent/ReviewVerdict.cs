namespace SreAgent;

// レビューの結果。承認なら PR 作成へ、却下なら理由と指摘（Findings）を添えて修正ループへ戻す。
public record ReviewVerdict(
    bool Approved,
    string Reason,
    IReadOnlyList<string> Findings)
{
    public static ReviewVerdict Approve(string reason) =>
        new(true, reason, Array.Empty<string>());

    public static ReviewVerdict Reject(string reason, IReadOnlyList<string> findings) =>
        new(false, reason, findings);
}
