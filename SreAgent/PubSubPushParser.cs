using System.Text;
using System.Text.Json;

namespace SreAgent;

// Pub/Sub の push リクエストから ErrorPayload を取り出すパーサー（ログ駆動の検知経路で使う）。
// リクエストの形: { "message": { "data": "<base64>", ... }, ... }
// data をデコードしたものは Cloud Logging の LogEntry か、ErrorPayload そのもの。どちらでも拾う。
public static class PubSubPushParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    // 復元できれば ErrorPayload を返す。形式が不正なら null。
    public static ErrorPayload? TryParse(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("message", out var msg)) return null;
            if (!msg.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String) return null;

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(dataEl.GetString()!));
            using var inner = JsonDocument.Parse(decoded);

            // LogEntry で包まれていれば jsonPayload を、そうでなければ全体を ErrorPayload とみなす。
            var payloadEl = inner.RootElement.TryGetProperty("jsonPayload", out var jp)
                ? jp
                : inner.RootElement;

            var payload = JsonSerializer.Deserialize<ErrorPayload>(payloadEl.GetRawText(), Options);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.RawJsonResponse) ||
                string.IsNullOrWhiteSpace(payload.FaultyMethodSource))
            {
                return null;
            }
            return payload;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
