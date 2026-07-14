using System.Text.Json;

namespace TargetApp;

// 外部の交通情報 API からデータを取得するサービス。
// FetchStatusAsync は AGENT-MANAGED マーカーで囲まれており、AI による自動修正の対象になる。
public class TransitService
{
    private readonly HttpClient _http;
    private readonly ILogger<TransitService> _logger;

    public TransitService(HttpClient http, ILogger<TransitService> logger)
    {
        _http = http;
        _logger = logger;
    }

    // API から取得した JSON を TrainStatus に変換して返す。
    // 変換に失敗したら、生 JSON を保持した TransitJsonException を投げる。
    // [AGENT-MANAGED-START: FetchStatusAsync]
    public record TrainStatusRaw
    {
        [JsonPropertyName("line_id")] public required string LineId { get; init; }
        [JsonPropertyName("line_name")] public required string LineName { get; init; }
        [JsonPropertyName("status")] public required string Status { get; init; }
        [JsonPropertyName("delays")] public required DelayInfo Delays { get; init; }
        [JsonPropertyName("last_updated")] public required DateTimeOffset LastUpdated { get; init; }

        public record DelayInfo { [JsonPropertyName("value")] public required int Value { get; init; } }
    }

    public async Task<TrainStatus> FetchStatusAsync()
    {
        var raw = await _http.GetStringAsync("/api/transit/status");
        _logger.LogInformation("Fetched {Length} bytes from transit API", raw.Length);
        try
        {
            var dto = JsonSerializer.Deserialize<TrainStatusRaw>(raw)
                ?? throw new TransitJsonException(raw, new JsonException("Deserialized result was null"));
            return new TrainStatus
            {
                LineId = dto.LineId,
                LineName = dto.LineName,
                Status = dto.Status,
                DelayMinutes = dto.Delays.Value,
                LastUpdated = dto.LastUpdated
            };
        }
        catch (JsonException ex) when (ex is not TransitJsonException)
        {
            throw new TransitJsonException(raw, ex);
        }
    }
    // [AGENT-MANAGED-END: FetchStatusAsync]
}
