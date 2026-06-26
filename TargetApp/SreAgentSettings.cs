using System.ComponentModel.DataAnnotations;

namespace TargetApp;

// SRE Agent への接続設定。
public record SreAgentSettings
{
    // SRE Agent の URL。絶対 URL でないと起動時に弾かれる。
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; init; } = "";

    // Webhook 送信のタイムアウト（秒）。
    public int TimeoutSeconds { get; init; } = 5;

    // Webhook 署名に使う共有秘密。空なら署名しない（SRE Agent 側も未設定なら検証しない）。
    public string WebhookSecret { get; init; } = "";
}
