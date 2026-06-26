using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// ErrorSignature のテスト: 値や順序が違っても構造が同じなら同じ署名、構造が違えば別の署名になること。
public class ErrorSignatureTests
{
    private static ErrorPayload Payload(string errorType, string rawJson) =>
        new(errorType, "msg", "stack", rawJson, "expected", "method source", "TargetApp/TransitService.cs");

    [Fact]
    public void Same_structure_different_values_yields_same_signature()
    {
        var a = Payload("JsonException", """{ "delays": { "value": 3 }, "line_id": "A" }""");
        var b = Payload("JsonException", """{ "delays": { "value": 99 }, "line_id": "ZZZ" }""");

        Assert.Equal(ErrorSignature.Compute(a), ErrorSignature.Compute(b));
    }

    [Fact]
    public void Different_key_order_yields_same_signature()
    {
        var a = Payload("JsonException", """{ "line_id": "A", "delays": { "value": 3 } }""");
        var b = Payload("JsonException", """{ "delays": { "value": 3 }, "line_id": "A" }""");

        Assert.Equal(ErrorSignature.Compute(a), ErrorSignature.Compute(b));
    }

    [Fact]
    public void Different_structure_yields_different_signature()
    {
        // delay_minutes（旧スキーマ）と delays.value（新スキーマ）は別物として区別したい。
        var before = Payload("JsonException", """{ "delay_minutes": 3 }""");
        var after = Payload("JsonException", """{ "delays": { "value": 3 } }""");

        Assert.NotEqual(ErrorSignature.Compute(before), ErrorSignature.Compute(after));
    }

    [Fact]
    public void Different_error_type_yields_different_signature()
    {
        var a = Payload("JsonException", """{ "x": 1 }""");
        var b = Payload("HttpRequestException", """{ "x": 1 }""");

        Assert.NotEqual(ErrorSignature.Compute(a), ErrorSignature.Compute(b));
    }

    [Fact]
    public void Signature_is_prefixed_with_error_type()
    {
        var sig = ErrorSignature.Compute(Payload("JsonException", """{ "x": 1 }"""));
        Assert.StartsWith("JsonException:", sig);
    }

    [Fact]
    public void Normalize_shape_ignores_values_and_order()
    {
        var s1 = ErrorSignature.NormalizeShape("""{ "b": 1, "a": "x" }""");
        var s2 = ErrorSignature.NormalizeShape("""{ "a": "different", "b": 2 }""");
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void Normalize_shape_falls_back_for_non_json()
    {
        // JSON にならない応答でも例外を投げずに署名化できること。
        var shape = ErrorSignature.NormalizeShape("<html>503 Service Unavailable</html>");
        Assert.False(string.IsNullOrEmpty(shape));
    }
}
