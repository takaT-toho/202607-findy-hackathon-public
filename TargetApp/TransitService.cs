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
    public record TransitResponseDto(
        [property: JsonPropertyName("line_id")] string LineId,
        [property: JsonPropertyName("line_name")] string LineName,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("delays")] JsonElement Delays,
        [property: JsonPropertyName("last_updated")] DateTimeOffset LastUpdated);

    public async Task<TrainStatus> FetchStatusAsync()
    {
        var raw = await _http.GetStringAsync("/api/transit/status");
        _logger.LogInformation("Fetched {Length} bytes from transit API", raw.Length);
        try
        {
            var dto = JsonSerializer.Deserialize<TransitResponseDto>(raw) 
                ?? throw new JsonException("Deserialized result was null");
            
            int delayMinutes = 0;
            if (dto.Delays.TryGetProperty("value", out var valueElement))
            {
                delayMinutes = valueElement.GetInt32();
            }

            return new TrainStatus
            {
                LineId = dto.LineId,
                LineName = dto.LineName,
                Status = dto.Status,
                DelayMinutes = delayMinutes,
                LastUpdated = dto.LastUpdated
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TransitJsonException(raw, ex);
        }
    }
// [AGENT-MANAGED-END: FetchStatusAsync]
}
