using System.Collections.Concurrent;

namespace SreAgent;

// IEventLog の既定実装。最新 100 件をメモリに保持する（再起動で消える、デモ用途）。
public class InMemoryEventLog : IEventLog
{
    private const int MaxEntries = 100;
    private readonly ConcurrentQueue<AgentEvent> _events = new();
    private readonly object _trimLock = new();

    public Task RecordAsync(EventType type, string summary, string? detail = null)
    {
        // CaseId は現在の処理コンテキスト（CaseContext）から自動で付ける。
        _events.Enqueue(new AgentEvent(DateTimeOffset.UtcNow, type, summary, detail, CaseContext.Current));

        // 100 件を超えたら古いものから捨てる（同時追加に備えてロックする）。
        if (_events.Count > MaxEntries)
        {
            lock (_trimLock)
            {
                while (_events.Count > MaxEntries && _events.TryDequeue(out _)) { }
            }
        }

        return Task.CompletedTask;
    }

    // 新しい順に最大 count 件を返す。
    public Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int count = 100)
    {
        var list = _events.Reverse().Take(count).ToList();
        return Task.FromResult<IReadOnlyList<AgentEvent>>(list);
    }
}
