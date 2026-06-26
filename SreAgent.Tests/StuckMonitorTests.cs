using Microsoft.Extensions.Logging.Abstractions;
using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// StuckMonitor のテスト: 詰まったケースをエスカレーションし、何度実行しても二重にはエスカレーションしない。
public class StuckMonitorTests
{
    private static async Task SeedFixingCaseAsync(InMemoryEventLog log, string caseId)
    {
        using (CaseContext.Begin(caseId))
        {
            await log.RecordAsync(EventType.WebhookReceived, "received");
            await log.RecordAsync(EventType.Triaged, "Classified as SchemaDrift (conf 90)");
            await log.RecordAsync(EventType.FixAttemptStarted, "attempt 1");
        }
    }

    [Fact]
    public async Task Escalates_stuck_case_and_is_idempotent()
    {
        var log = new InMemoryEventLog();
        await SeedFixingCaseAsync(log, "X");

        var monitor = new StuckMonitor(log,
            interval: TimeSpan.FromMinutes(1),
            threshold: TimeSpan.FromMinutes(10),
            logger: NullLogger<StuckMonitor>.Instance);

        // イベントは「今」記録したので、20分後の時計で見れば閾値(10分)を超えて詰まっている。
        var future = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20);

        var first = await monitor.ScanOnceAsync(future);
        Assert.Equal(1, first); // 1件エスカレーションする

        var second = await monitor.ScanOnceAsync(future);
        Assert.Equal(0, second); // 既に終端なので再エスカレーションしない

        var cases = CaseProjection.Fold(await log.GetRecentAsync(int.MaxValue));
        Assert.Equal(CaseState.Escalated, Assert.Single(cases).State);
    }

    [Fact]
    public async Task Does_not_escalate_fresh_case()
    {
        var log = new InMemoryEventLog();
        await SeedFixingCaseAsync(log, "Y");

        var monitor = new StuckMonitor(log,
            interval: TimeSpan.FromMinutes(1),
            threshold: TimeSpan.FromMinutes(10),
            logger: NullLogger<StuckMonitor>.Instance);

        // 1分後に見れば閾値内なので詰まっていない。
        var soon = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        Assert.Equal(0, await monitor.ScanOnceAsync(soon));
    }
}
