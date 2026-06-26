using System.Text.Json.Serialization;

namespace TargetApp;

// 外部 API から取得する交通情報のデータモデル（変更前のスキーマ）。
// required のプロパティが JSON に無いと JsonException になり、これがスキーマ変更検知のきっかけになる。
public record TrainStatus
{
    [JsonPropertyName("line_id")]
    public required string LineId { get; init; }

    [JsonPropertyName("line_name")]
    public required string LineName { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("delay_minutes")]
    public required int DelayMinutes { get; init; }

    [JsonPropertyName("last_updated")]
    public required DateTimeOffset LastUpdated { get; init; }
}
