using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// FalsePositiveGate のテスト: 一過性障害を弾きつつ、本物のスキーマ変更は弾かない（保留する）ことを確認する。
public class FalsePositiveGateTests
{
    private static ErrorPayload Payload(string errorType, string rawJson) =>
        new(errorType, "msg", "stack", rawJson, "expected", "method source", "TargetApp/TransitService.cs");

    // 通信・タイムアウト系の例外型は一過性として弾く。

    [Theory]
    [InlineData("HttpRequestException")]
    [InlineData("System.Net.Http.HttpRequestException")]
    [InlineData("TaskCanceledException")]
    [InlineData("TimeoutException")]
    [InlineData("SocketException")]
    public void Transient_exception_types_are_rejected(string errorType)
    {
        var verdict = FalsePositiveGate.Inspect(Payload(errorType, """{ "x": 1 }"""));

        Assert.NotNull(verdict);
        Assert.False(verdict!.IsAiFixable);
        Assert.Equal(ErrorClass.TransientUpstream, verdict.Class);
    }

    // JSON にならない応答（HTML エラーページ等）は一過性として弾く。

    [Theory]
    [InlineData("<html><body>503 Service Unavailable</body></html>")]
    [InlineData("502 Bad Gateway")]
    [InlineData("upstream connect error or disconnect/reset before headers")]
    public void Non_json_response_is_rejected(string raw)
    {
        var verdict = FalsePositiveGate.Inspect(Payload("JsonException", raw));

        Assert.NotNull(verdict);
        Assert.False(verdict!.IsAiFixable);
        Assert.Equal(ErrorClass.TransientUpstream, verdict.Class);
    }

    [Fact]
    public void Empty_response_body_is_rejected()
    {
        var verdict = FalsePositiveGate.Inspect(Payload("JsonException", ""));

        Assert.NotNull(verdict);
        Assert.Equal(ErrorClass.TransientUpstream, verdict!.Class);
    }

    // エラー応答だけ（業務データ無し）は一過性として弾く。

    [Theory]
    [InlineData("""{ "error": "Internal Server Error", "statusCode": 500 }""")]
    [InlineData("""{ "message": "Too Many Requests", "code": "RATE_LIMITED" }""")]
    [InlineData("""{ "title": "Bad Gateway", "status": 502, "detail": "upstream timeout" }""")]
    public void Error_envelope_is_rejected(string raw)
    {
        var verdict = FalsePositiveGate.Inspect(Payload("JsonException", raw));

        Assert.NotNull(verdict);
        Assert.False(verdict!.IsAiFixable);
        Assert.Equal(ErrorClass.TransientUpstream, verdict.Class);
    }

    // 本物のスキーマ変更・業務データは弾かず、分類器に委ねる（null を返す）。

    [Fact]
    public void Real_schema_drift_is_not_rejected()
    {
        // 新スキーマの業務データ。これを誤って棄却すると本来直すべきものを取りこぼす。
        var raw = """{ "delays": { "value": 3 }, "line_id": "A" }""";

        Assert.Null(FalsePositiveGate.Inspect(Payload("JsonException", raw)));
    }

    [Fact]
    public void Business_data_with_message_field_is_not_rejected()
    {
        // "message" はエラー応答のキーだが、業務データ(trainStatus)が混ざるのでエラー応答とは断定しない。
        var raw = """{ "trainStatus": "delayed", "message": "3 min delay" }""";

        Assert.Null(FalsePositiveGate.Inspect(Payload("JsonException", raw)));
    }

    [Fact]
    public void Json_array_payload_is_not_rejected()
    {
        // 配列はエラー応答判定の対象外（業務データの可能性）なので分類器へ委ねる。
        var raw = """[ { "line_id": "A" }, { "line_id": "B" } ]""";

        Assert.Null(FalsePositiveGate.Inspect(Payload("JsonException", raw)));
    }

    // 同じ入力なら同じ結果になること。

    [Fact]
    public void Inspect_is_deterministic()
    {
        var p = Payload("JsonException", """{ "error": "boom", "statusCode": 500 }""");

        Assert.Equal(FalsePositiveGate.Inspect(p)!.Class, FalsePositiveGate.Inspect(p)!.Class);
    }
}
