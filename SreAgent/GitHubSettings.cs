using System.ComponentModel.DataAnnotations;

namespace SreAgent;

// GitHub への接続設定（appsettings から注入する）。
public record GitHubSettings
{
    [Required(AllowEmptyStrings = false)]
    public string PersonalAccessToken { get; init; } = "";

    [Required(AllowEmptyStrings = false)]
    public string RepoOwner { get; init; } = "";

    [Required(AllowEmptyStrings = false)]
    public string RepoName { get; init; } = "";

    public string BaseBranch { get; init; } = "main";

    // 同じエラーをこの分数だけ再処理しない（重複処理のコスト削減用）。
    [Range(0, 1440)]
    public int DedupWindowMinutes { get; init; } = 60;
}
