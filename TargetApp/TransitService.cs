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
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<TrainStatusResponse>(raw, options);
            if (response == null) throw new JsonException("Deserialized result was null");
            
            return new TrainStatus
            {
                LineId = response.LineId,
                LineName = response.LineName,
                Status = response.Status,
                DelayMinutes = response.Delays?.Value ?? 0,
                LastUpdated = response.LastUpdated
            };
        }
        catch (JsonException ex) when (ex is not TransitJsonException)
        {
            throw new TransitJsonException(raw, ex);
        }
    }

    private record DelaysDto(
        [property: JsonPropertyName("value")] int Value,
        [property: JsonPropertyName("unit")] string Unit);

    private record TrainStatusResponse(
        [property: JsonPropertyName("line_id")] string LineId,
        [property: JsonPropertyName("line_name")] string LineName,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("delays")] DelaysDto? Delays,
        [property: JsonPropertyName("last_updated")] DateTimeOffset LastUpdated);
// [AGENT-MANAGED-END: FetchStatusAsync]
}
