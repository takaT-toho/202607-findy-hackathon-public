using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// FileEventLog のテスト: 再起動をまたいでイベントが保持・復元されること、上限件数で打ち切られること。
public class FileEventLogTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sre-eventlog-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task Events_survive_a_simulated_restart()
    {
        var path = TempPath();
        try
        {
            var first = new FileEventLog(path, maxEntries: 100);
            using (CaseContext.Begin("CASE-1"))
            {
                await first.RecordAsync(EventType.WebhookReceived, "received", "msg");
                await first.RecordAsync(EventType.Triaged, "Classified as SchemaDrift (conf 90)");
                await first.RecordAsync(EventType.PrCreated, "https://github.com/x/y/pull/1");
            }

            // 別インスタンスを作る = 再起動相当。ファイルから読み戻す。
            var restarted = new FileEventLog(path, maxEntries: 100);
            var events = await restarted.GetRecentAsync(100);

            Assert.Equal(3, events.Count);
            Assert.All(events, e => Assert.Equal("CASE-1", e.CaseId));

            // ケース集計も再起動後に復元できる。
            var c = Assert.Single(CaseProjection.Fold(events));
            Assert.Equal(CaseState.PrOpened, c.State);
            Assert.Equal(ErrorClass.SchemaDrift, c.Class);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Caps_at_max_entries()
    {
        var path = TempPath();
        try
        {
            var log = new FileEventLog(path, maxEntries: 3);
            for (var i = 0; i < 10; i++)
            {
                await log.RecordAsync(EventType.WebhookReceived, $"e{i}");
            }

            var restarted = new FileEventLog(path, maxEntries: 3);
            var events = await restarted.GetRecentAsync(100);

            Assert.Equal(3, events.Count);
            // 最新3件（e9, e8, e7）が降順で残る。
            Assert.Equal("e9", events[0].Summary);
            Assert.Equal("e7", events[2].Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
