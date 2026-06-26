namespace SreAgent;

// 決定論ゲート（FalsePositiveGate）で判別できなかったエラーを分類する。
// 開発環境では StubTriageClassifier、本番では LLM を使う TriageClassifier が使われる。
public interface ITriageClassifier
{
    // エラーを分類し、AI で修正できるかどうかを含む判定結果を返す。失敗時は安全側（人間対応）に倒す。
    Task<TriageVerdict> ClassifyAsync(ErrorPayload payload);
}
