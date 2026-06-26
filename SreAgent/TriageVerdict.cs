namespace SreAgent;

// 発生したエラーの分類。
public enum ErrorClass
{
    // 外部 API の JSON スキーマが変わった。Before/After の差分から修正できる。
    SchemaDrift,

    // 構造は同じだが、enum 値のリネームや単位・フォーマットの差で値のマッピングに失敗した。
    MappingFault,

    // 上流のエラー応答や通信不達。コード修正では直らない一過性障害なので棄却する。
    TransientUpstream,

    // 影響範囲が広い・曖昧で機械修正の確信が持てない。人間にエスカレーションする。
    NeedsHuman,

    // 分類できなかった。安全側に倒して人間にエスカレーションする。
    Unknown
}

// Triage の判定結果。WebhookReceiver はこれを見て AI/PR を起動するか棄却するか決める。
// IsAiFixable が true になるのは SchemaDrift / MappingFault のときだけ。
public record TriageVerdict(
    ErrorClass Class,
    bool IsAiFixable,
    string Reason,
    int Confidence)
{
    // AI で直すべき分類の判定を作る。
    public static TriageVerdict Fixable(ErrorClass cls, string reason, int confidence) =>
        new(cls, IsAiFixable: true, reason, confidence);

    // AI では直せない・人間に委ねる分類の判定を作る。
    public static TriageVerdict NotFixable(ErrorClass cls, string reason, int confidence) =>
        new(cls, IsAiFixable: false, reason, confidence);

    // 分類だけが分かっているとき、IsAiFixable を分類から自動で決めて判定を作る。
    public static TriageVerdict FromClass(ErrorClass cls, string reason, int confidence)
    {
        var fixable = cls is ErrorClass.SchemaDrift or ErrorClass.MappingFault;
        return new(cls, fixable, reason, confidence);
    }
}
