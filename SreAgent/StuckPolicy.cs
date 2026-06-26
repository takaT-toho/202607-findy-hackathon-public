namespace SreAgent;

// ケースが「進まず詰まっている」かを判定する純関数。StuckMonitor が使う。
public static class StuckPolicy
{
    // これ以上進まない終端状態（詰まりとはみなさない）。
    private static readonly HashSet<CaseState> Terminal = new()
    {
        CaseState.PrOpened, CaseState.Failed, CaseState.Rejected, CaseState.Escalated
    };

    // 終端状態でなく、最終更新から threshold を超えて止まっていれば詰まっていると判定する。
    public static bool IsStuck(CaseState state, DateTimeOffset lastUpdated, DateTimeOffset now, TimeSpan threshold)
    {
        if (Terminal.Contains(state))
        {
            return false;
        }
        return now - lastUpdated > threshold;
    }

    // 詰まっているケースだけを抜き出して返す。
    public static IReadOnlyList<CaseView> FindStuck(
        IReadOnlyList<CaseView> cases, DateTimeOffset now, TimeSpan threshold)
        => cases.Where(c => IsStuck(c.State, c.LastUpdated, now, threshold)).ToList();
}
