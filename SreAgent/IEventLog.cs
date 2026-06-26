namespace SreAgent;

// エージェントの操作履歴を記録・取得する。ダッシュボード表示に使う。
public interface IEventLog
{
    Task RecordAsync(EventType type, string summary, string? detail = null);
    Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int count = 100);
}
