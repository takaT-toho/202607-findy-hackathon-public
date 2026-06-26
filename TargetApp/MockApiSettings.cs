using System.ComponentModel.DataAnnotations;

namespace TargetApp;

// 外部 API（モック）への接続設定。BaseUrl は絶対 URL でないと起動時に弾かれる。
public record MockApiSettings
{
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; init; } = "";
}
