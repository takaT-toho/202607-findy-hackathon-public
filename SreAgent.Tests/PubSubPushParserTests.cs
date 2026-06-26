using System.Text;
using System.Text.Json;
using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// Pub/Sub のリクエストから ErrorPayload を復元するパーサーのテスト（正常系と不正入力）。
public class PubSubPushParserTests
{
    private static JsonElement Envelope(string innerJson)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerJson));
        var body = $$"""{ "message": { "data": "{{data}}" }, "subscription": "projects/x/subscriptions/y" }""";
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private const string ValidPayload = """
        {
          "ErrorType": "JsonException",
          "ErrorMessage": "drift",
          "StackTrace": "at ...",
          "RawJsonResponse": "{\"delays\":{\"value\":3}}",
          "ExpectedSchema": "record TrainStatus",
          "FaultyMethodSource": "public async Task FetchStatusAsync() {}",
          "FilePath": "TargetApp/TransitService.cs"
        }
        """;

    [Fact]
    public void Parses_raw_error_payload_in_message_data()
    {
        var payload = PubSubPushParser.TryParse(Envelope(ValidPayload));
        Assert.NotNull(payload);
        Assert.Equal("JsonException", payload!.ErrorType);
        Assert.Equal("TargetApp/TransitService.cs", payload.FilePath);
    }

    [Fact]
    public void Parses_payload_wrapped_in_log_entry_jsonPayload()
    {
        var logEntry = $$"""{ "severity": "ERROR", "jsonPayload": {{ValidPayload}} }""";
        var payload = PubSubPushParser.TryParse(Envelope(logEntry));
        Assert.NotNull(payload);
        Assert.Equal("JsonException", payload!.ErrorType);
    }

    [Fact]
    public void Is_case_insensitive_on_property_names()
    {
        var lower = ValidPayload.Replace("ErrorType", "errorType").Replace("RawJsonResponse", "rawJsonResponse")
            .Replace("FaultyMethodSource", "faultyMethodSource");
        var payload = PubSubPushParser.TryParse(Envelope(lower));
        Assert.NotNull(payload);
    }

    [Fact]
    public void Returns_null_when_message_missing()
    {
        var body = JsonDocument.Parse("""{ "subscription": "x" }""").RootElement;
        Assert.Null(PubSubPushParser.TryParse(body));
    }

    [Fact]
    public void Returns_null_when_data_is_not_base64()
    {
        var body = JsonDocument.Parse("""{ "message": { "data": "!!!not-base64!!!" } }""").RootElement;
        Assert.Null(PubSubPushParser.TryParse(body));
    }

    [Fact]
    public void Returns_null_when_required_fields_missing()
    {
        // RawJsonResponse が欠けているので不完全とみなして null。
        var incomplete = """{ "ErrorType": "JsonException", "FaultyMethodSource": "x" }""";
        Assert.Null(PubSubPushParser.TryParse(Envelope(incomplete)));
    }
}
