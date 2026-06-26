using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// 同時実行ゲートの挙動を確認する: 上限を超えて確保できない／解放で再び確保できる。
public class ConcurrencyGateTests
{
    private static readonly TimeSpan NoWait = TimeSpan.Zero;

    [Fact]
    public async Task Acquires_up_to_limit_then_refuses()
    {
        var gate = new SemaphoreConcurrencyGate(maxConcurrent: 2);

        Assert.True(await gate.TryAcquireAsync(NoWait));
        Assert.True(await gate.TryAcquireAsync(NoWait));
        // 上限に達したら待たずに false を返す（ブロックしない）。
        Assert.False(await gate.TryAcquireAsync(NoWait));
        Assert.Equal(0, gate.AvailableSlots);
    }

    [Fact]
    public async Task Release_frees_a_slot()
    {
        var gate = new SemaphoreConcurrencyGate(maxConcurrent: 1);

        Assert.True(await gate.TryAcquireAsync(NoWait));
        Assert.False(await gate.TryAcquireAsync(NoWait));

        gate.Release();
        Assert.Equal(1, gate.AvailableSlots);
        Assert.True(await gate.TryAcquireAsync(NoWait));
    }

    [Fact]
    public void Nonpositive_limit_is_clamped_to_one()
    {
        var gate = new SemaphoreConcurrencyGate(maxConcurrent: 0);
        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public async Task Waiting_acquire_succeeds_once_a_slot_is_released()
    {
        var gate = new SemaphoreConcurrencyGate(maxConcurrent: 1);
        Assert.True(await gate.TryAcquireAsync(NoWait));

        var waiter = gate.TryAcquireAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiter.IsCompleted); // まだ空きが無いので待っている
        gate.Release();

        Assert.True(await waiter); // 解放で待機が解ける
    }
}
