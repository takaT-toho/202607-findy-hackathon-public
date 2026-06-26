namespace SreAgent;

// 受信したエラーを分類し「AI で直すべきか」を判断する段。
// AI で直せない（IsAiFixable=false）なら、AI 呼び出しや PR 作成は行わない。
public interface IErrorTriager
{
    // まず決定論ゲートで判断し、確定できないときだけ分類器に委ねる。失敗時は安全側（人間対応）に倒す。
    Task<TriageVerdict> TriageAsync(ErrorPayload payload);
}
