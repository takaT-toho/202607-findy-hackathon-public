using System.Text.Json;

namespace SreAgent;

// イベントをファイル（JSONL）に保存する IEventLog 実装。再起動してもダッシュボードのケースが復元される。
// 最新 maxEntries 件だけを保持し、記録のたびに全体を一時ファイル経由で安全に書き換える。
public sealed class FileEventLog : IEventLog
{
    private const int DefaultMax = 500;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly int _max;
    private readonly object _lock = new();
    private readonly LinkedList<AgentEvent> _events = new();

    public FileEventLog(string path, int maxEntries = DefaultMax)
    {
        _path = path;
        _max = maxEntries < 1 ? 1 : maxEntries;
        Load();
    }

    public Task RecordAsync(EventType type, string summary, string? detail = null)
    {
        var ev = new AgentEvent(DateTimeOffset.UtcNow, type, summary, detail, CaseContext.Current);
        lock (_lock)
        {
            _events.AddLast(ev);
            Trim();
            Persist();
        }
        return Task.CompletedTask;
    }

    // 新しい順に最大 count 件を返す（InMemoryEventLog と同じ振る舞い）。
    public Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int count = 100)
    {
        lock (_lock)
        {
            var list = _events.Reverse().Take(count).ToList();
            return Task.FromResult<IReadOnlyList<AgentEvent>>(list);
        }
    }

    // 起動時に既存ファイルを読み込む。
    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var ev = JsonSerializer.Deserialize<AgentEvent>(line, Json);
                if (ev is not null)
                {
                    _events.AddLast(ev);
                }
            }
            catch (JsonException)
            {
                // 壊れた行は読み飛ばす。
            }
        }
        Trim();
    }

    // 上限を超えた古いイベントを捨てる。
    private void Trim()
    {
        while (_events.Count > _max)
        {
            _events.RemoveFirst();
        }
    }

    // 一時ファイルに書いてから本ファイルへ置き換える（書き込み途中で壊れないように）。
    private void Persist()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, _events.Select(e => JsonSerializer.Serialize(e, Json)));
        File.Move(tmp, _path, overwrite: true);
    }
}
