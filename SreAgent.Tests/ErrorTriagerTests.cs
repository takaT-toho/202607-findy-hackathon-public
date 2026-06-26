using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// ErrorTriager のテスト: 決定論ゲートで決まるときは分類器を呼ばず、保留のときだけ分類器に委ねる。
public class ErrorTriagerTests
{
    private static ErrorPayload Payload(string errorType, string rawJson) =>
        new(errorType, "msg", "stack", rawJson, "expected", "method source", "TargetApp/TransitService.cs");

    // 呼ばれたら例外を投げる。分類器が呼ばれていないことを確認するために使う。
    private sealed class ThrowingClassifier : ITriageClassifier
    {
        public bool WasCalled { get; private set; }
        public Task<TriageVerdict> ClassifyAsync(ErrorPayload payload)
        {
            WasCalled = true;
            throw new Xunit.Sdk.XunitException("classifier must not be called when the gate decides");
        }
    }

    // 常に固定の判定を返す。分類器に委譲されたことを確認するために使う。
    private sealed class FixedClassifier(TriageVerdict verdict) : ITriageClassifier
    {
        public bool WasCalled { get; private set; }
        public Task<TriageVerdict> ClassifyAsync(ErrorPayload payload)
        {
            WasCalled = true;
            return Task.FromResult(verdict);
        }
    }

    [Fact]
    public async Task Gate_rejection_short_circuits_classifier()
    {
        var spy = new ThrowingClassifier();
        var triager = new ErrorTriager(spy);

        // 通信系の例外なのでゲートが一過性障害と確定し、分類器は呼ばれない。
        var verdict = await triager.TriageAsync(Payload("HttpRequestException", """{ "x": 1 }"""));

        Assert.False(spy.WasCalled);
        Assert.False(verdict.IsAiFixable);
        Assert.Equal(ErrorClass.TransientUpstream, verdict.Class);
    }

    [Fact]
    public async Task Ambiguous_payload_is_delegated_to_classifier()
    {
        var fake = new FixedClassifier(TriageVerdict.FromClass(ErrorClass.SchemaDrift, "drift", 90));
        var triager = new ErrorTriager(fake);

        // 業務データなのでゲートは保留し、分類器に委譲されてその判定が返る。
        var verdict = await triager.TriageAsync(Payload("JsonException", """{ "delays": { "value": 3 } }"""));

        Assert.True(fake.WasCalled);
        Assert.True(verdict.IsAiFixable);
        Assert.Equal(ErrorClass.SchemaDrift, verdict.Class);
    }

    [Fact]
    public async Task Stub_classifier_keeps_dev_flow_fixable()
    {
        var triager = new ErrorTriager(new StubTriageClassifier());

        var verdict = await triager.TriageAsync(Payload("JsonException", """{ "delays": { "value": 3 } }"""));

        Assert.True(verdict.IsAiFixable);
        Assert.Equal(ErrorClass.SchemaDrift, verdict.Class);
    }
}
