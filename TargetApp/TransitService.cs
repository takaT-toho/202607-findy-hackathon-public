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
        record TransitResponse(string line_id, string line_name, string status, DelayInfo delays, DateTimeOffset last_updated)
        {
            public TrainStatus ToTrainStatus() => new() 
            {
                LineId = line_id,
                LineName = line_name,
                Status = status,
                DelayMinutes = delays.value,
                LastUpdated = last_updated
            };
        }
        record DelayInfo(int value, string unit);

        var raw = await _http.GetStringAsync("/api/transit/status");
        _logger.LogInformation("Fetched {Length} bytes from transit API", raw.Length);
        try
        {
            var dto = JsonSerializer.Deserialize<TransitResponse>(raw);
            return dto?.ToTrainStatus() 
                ?? throw new TransitJsonException(raw, new JsonException("Deserialized result was null"));
        }
        catch (JsonException ex) when (ex is not TransitJsonException)
        {
            throw new TransitJsonException(raw, ex);
        }
    }
    // [AGENT-MANAGED-END: FetchStatusAsync]
}
