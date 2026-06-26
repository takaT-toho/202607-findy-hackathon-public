using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TargetApp;

// SRE Agent の Webhook を呼ぶクライアント。
// 通知に失敗してもアプリ本体の処理は止めない（fire-and-forget）。
public class SreAgentClient
{
    // 送信本文の形式を SRE Agent 側のモデルと一致させ、署名対象の本文も固定するため明示的に指定する。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _webhookSecret;
    private readonly ILogger<SreAgentClient> _logger;

    public SreAgentClient(HttpClient http, IOptions<SreAgentSettings> options, ILogger<SreAgentClient> logger)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
        _webhookSecret = options.Value.WebhookSecret;
        _logger = logger;
    }

    // SRE Agent の /webhook/error に payload を POST する。失敗は例外にせずログだけ残す。
    public async Task NotifyErrorAsync(ErrorPayload payload)
    {
        try
        {
            // 署名は実際に送る本文に対して計算する必要があるので、先に本文を文字列化しておく。
            var body = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook/error")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(_webhookSecret))
            {
                request.Headers.Add("X-Signature", WebhookSignature.Compute(_webhookSecret, body));
            }

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("SRE Agent webhook accepted with status {Status}", (int)response.StatusCode);
            }
            else
            {
                _logger.LogWarning("SRE Agent webhook returned {Status}", (int)response.StatusCode);
            }
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "SRE Agent webhook timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SRE Agent webhook connection failed");
        }
    }
}
