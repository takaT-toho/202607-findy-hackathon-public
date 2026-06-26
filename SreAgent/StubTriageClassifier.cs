namespace SreAgent;

// 開発環境用の分類器。LLM を呼ばず、常に「AI で修正できる（SchemaDrift）」と判定する。
public class StubTriageClassifier : ITriageClassifier
{
    public Task<TriageVerdict> ClassifyAsync(ErrorPayload payload) =>
        Task.FromResult(TriageVerdict.FromClass(
            ErrorClass.SchemaDrift,
            "[STUB] 決定論ゲートを通過したため schema drift とみなして修正フローへ流します。",
            confidence: 80));
}
