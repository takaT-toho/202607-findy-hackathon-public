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
    public async Task<TrainStatus> FetchStatusAsync()
    {
        var raw = await _http.GetStringAsync("/api/transit/status");
        _logger.LogInformation("Fetched {Length} bytes from transit API", raw.Length);
        try
        {
            var dto = JsonSerializer.Deserialize<TransitResponseDto>(raw);
            if (dto == null) throw new JsonException("Deserialized result was null");

            return new TrainStatus
            {
                LineId = dto.LineId,
                LineName = dto.LineName,
                Status = dto.Status,
                DelayMinutes = dto.Delays?.Value ?? 0,
                LastUpdated = dto.LastUpdated
            };
        }
        catch (JsonException ex) when (ex is not TransitJsonException)
        {
            throw new TransitJsonException(raw, ex);
        }
    }
    // [AGENT-MANAGED-END: FetchStatusAsync]

    private record TransitResponseDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("line_id")] string LineId,
        [property: System.Text.Json.Serialization.JsonPropertyName("line_name")] string LineName,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("delays")] TransitDelayDto? Delays,
        [property: System.Text.Json.Serialization.JsonPropertyName("last_updated")] DateTimeOffset LastUpdated
    );

    private record TransitDelayDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("value")] int Value
    );
}
