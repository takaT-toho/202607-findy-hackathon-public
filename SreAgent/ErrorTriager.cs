namespace SreAgent;

// IErrorTriager の実装。2 段構えでエラーを判定する:
//   1. FalsePositiveGate（決定論）で明らかな一過性障害を弾く
//   2. ゲートが判断できなければ分類器（LLM/Stub）に委ねる
// これで「誤検知ガードは必ず決定論で効く」状態を保つ。
public class ErrorTriager : IErrorTriager
{
    private readonly ITriageClassifier _classifier;

    public ErrorTriager(ITriageClassifier classifier)
    {
        _classifier = classifier;
    }

    public async Task<TriageVerdict> TriageAsync(ErrorPayload payload)
    {
        var gated = FalsePositiveGate.Inspect(payload);
        if (gated is not null)
        {
            return gated;
        }

        return await _classifier.ClassifyAsync(payload);
    }
}
