using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// Webhook 署名のテスト: 正しい署名だけ受理、改ざん・欠落は拒否、秘密未設定なら検証しない。
public class WebhookSignatureTests
{
    private const string Secret = "top-secret-key";
    private const string Body = """{"errorType":"JsonException","x":1}""";

    [Fact]
    public void Compute_is_deterministic_and_prefixed()
    {
        var a = WebhookSignature.Compute(Secret, Body);
        var b = WebhookSignature.Compute(Secret, Body);
        Assert.Equal(a, b);
        Assert.StartsWith("sha256=", a);
    }

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var sig = WebhookSignature.Compute(Secret, Body);
        Assert.True(WebhookSignature.Verify(Secret, Body, sig));
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var sig = WebhookSignature.Compute(Secret, Body);
        Assert.False(WebhookSignature.Verify(Secret, Body + " ", sig));
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var sig = WebhookSignature.Compute("other-key", Body);
        Assert.False(WebhookSignature.Verify(Secret, Body, sig));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=deadbeef")]
    public void Missing_or_garbage_signature_is_rejected_when_secret_set(string? provided)
    {
        Assert.False(WebhookSignature.Verify(Secret, Body, provided));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verification_is_disabled_when_no_secret_configured(string? secret)
    {
        // 秘密が未設定なら署名が無くても受理する（ローカル開発用）。
        Assert.True(WebhookSignature.Verify(secret, Body, null));
    }
}
