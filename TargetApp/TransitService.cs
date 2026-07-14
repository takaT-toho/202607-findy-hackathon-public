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
        record TransitDto(string line_id, string line_name, string status, Dictionary<string, JsonElement> delays, DateTimeOffset last_updated);

        var raw = await _http.GetStringAsync("/api/transit/status");
        _logger.LogInformation("Fetched {Length} bytes from transit API", raw.Length);
        try
        {
            var dto = JsonSerializer.Deserialize<TransitDto>(raw) ?? throw new TransitJsonException(raw, new JsonException("Deserialized result was null"));
            return new TrainStatus
            {
                LineId = dto.line_id,
                LineName = dto.line_name,
                Status = dto.status,
                DelayMinutes = dto.delays.TryGetValue("value", out var val) && val.TryGetInt32(out var minutes) ? minutes : 0,
                LastUpdated = dto.last_updated
            };
        }
        catch (JsonException ex) when (ex is not TransitJsonException)
        {
            throw new TransitJsonException(raw, ex);
        }
    }
    // [AGENT-MANAGED-END: FetchStatusAsync]
}
