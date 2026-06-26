using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// 重複抑制ゲートの挙動を確認する。時刻は差し替えたクロックで進める。
public class InMemoryDedupGateTests
{
    [Fact]
    public void First_acquire_succeeds()
    {
        var gate = new InMemoryDedupGate(TimeSpan.FromMinutes(60));
        Assert.True(gate.TryAcquire("sig"));
    }

    [Fact]
    public void Second_acquire_within_window_is_suppressed()
    {
        var now = new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero);
        var gate = new InMemoryDedupGate(TimeSpan.FromMinutes(60), () => now);

        Assert.True(gate.TryAcquire("sig"));
        now = now.AddMinutes(30); // 窓内
        Assert.False(gate.TryAcquire("sig"));
    }

    [Fact]
    public void Acquire_after_window_succeeds_again()
    {
        var now = new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero);
        var gate = new InMemoryDedupGate(TimeSpan.FromMinutes(60), () => now);

        Assert.True(gate.TryAcquire("sig"));
        now = now.AddMinutes(61); // 窓外
        Assert.True(gate.TryAcquire("sig"));
    }

    [Fact]
    public void Release_allows_immediate_reacquire()
    {
        var gate = new InMemoryDedupGate(TimeSpan.FromMinutes(60));
        Assert.True(gate.TryAcquire("sig"));
        gate.Release("sig");
        Assert.True(gate.TryAcquire("sig"));
    }

    [Fact]
    public void Distinct_signatures_are_independent()
    {
        var gate = new InMemoryDedupGate(TimeSpan.FromMinutes(60));
        Assert.True(gate.TryAcquire("sig-a"));
        Assert.True(gate.TryAcquire("sig-b"));
    }
}
