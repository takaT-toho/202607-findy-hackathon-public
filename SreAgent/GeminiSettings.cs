using System.ComponentModel.DataAnnotations;

namespace SreAgent;

// Gemini への接続設定（appsettings から注入する）。
public record GeminiSettings
{
    [Required(AllowEmptyStrings = false)]
    public string ProjectId { get; init; } = "";

    public string Region { get; init; } = "asia-northeast1";

    public string ModelId { get; init; } = "gemini-3.1-flash-lite";

    // ビルド/テストが通るまで AI に修正を作り直させる最大回数。1 なら 1 回きり。
    [Range(1, 10)]
    public int MaxFixAttempts { get; init; } = 3;
}
