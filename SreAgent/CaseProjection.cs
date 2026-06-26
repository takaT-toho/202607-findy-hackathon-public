namespace SreAgent;

// 記録済みのイベント列を「エラーケース単位の状態」に集計する読み取りモデル。
// ダッシュボードの一覧表示が使う。新しく状態を保持するのではなく、イベントから毎回導出する。
public static class CaseProjection
{
    // CaseId ごとにイベントをまとめ、各ケースの CaseView を作る。最近動いた順に並べて返す。
    public static IReadOnlyList<CaseView> Fold(IReadOnlyList<AgentEvent> events)
    {
        return events
            .Where(e => !string.IsNullOrEmpty(e.CaseId))
            .GroupBy(e => e.CaseId!)
            .Select(FoldOne)
            .OrderByDescending(c => c.LastUpdated)
            .ToList();
    }

    // 1 ケース分のイベントから CaseView を組み立てる。
    private static CaseView FoldOne(IGrouping<string, AgentEvent> group)
    {
        var ordered = group.OrderBy(e => e.Timestamp).ToList();

        var triaged = ordered.FirstOrDefault(e => e.Type == EventType.Triaged);
        var pr = ordered.FirstOrDefault(e => e.Type == EventType.PrCreated);

        return new CaseView(
            CaseId: group.Key,
            Class: triaged is null ? null : ParseClass(triaged.Summary),
            State: DeriveState(ordered),
            FixAttempts: ordered.Count(e => e.Type == EventType.FixAttemptStarted),
            TriageConfidence: triaged is null ? null : ParseConfidence(triaged.Summary),
            PrUrl: pr is null ? null : ExtractUrl(pr.Summary) ?? ExtractUrl(pr.Detail),
            FirstSeen: ordered[0].Timestamp,
            LastUpdated: ordered[^1].Timestamp,
            Timeline: ordered);
    }

    // 現在の状態を決める。
    //  1. 終端イベント（成功/失敗/棄却/エスカレ）があれば最優先で確定する（後続イベントで巻き戻さない）。
    //  2. 終端していなければ、進捗イベントを古い順にたどり「最後の進捗」を採用する。
    private static CaseState DeriveState(IReadOnlyList<AgentEvent> ordered)
    {
        var types = ordered.Select(e => e.Type).ToHashSet();
        if (types.Contains(EventType.PrCreated)) return CaseState.PrOpened;
        if (types.Contains(EventType.PrFailed)) return CaseState.Failed;
        if (types.Contains(EventType.TriageRejected)) return CaseState.Rejected;
        if (types.Contains(EventType.Escalated)) return CaseState.Escalated;

        var state = CaseState.Detected;
        foreach (var ev in ordered)
        {
            var mapped = MapProgress(ev.Type);
            if (mapped is not null) state = mapped.Value;
        }
        return state;
    }

    // 進捗を表すイベントを状態に対応づける。状態を進めないイベントは null（状態を据え置く）。
    private static CaseState? MapProgress(EventType type) => type switch
    {
        EventType.Triaged => CaseState.Triaged,
        EventType.FixAttemptStarted => CaseState.Fixing,
        EventType.BuildVerifyCompleted or EventType.TestVerifyCompleted => CaseState.Verified,
        EventType.ReviewStarted or EventType.ReviewApproved or EventType.ReviewRejected => CaseState.Reviewing,
        _ => null
    };

    // Triaged イベントの Summary からエラー分類名を拾う。
    private static ErrorClass? ParseClass(string summary)
    {
        foreach (var cls in Enum.GetValues<ErrorClass>())
        {
            if (summary.Contains(cls.ToString(), StringComparison.Ordinal))
            {
                return cls;
            }
        }
        return null;
    }

    // Summary 内の "conf {数値}" から確信度を拾う。見つからなければ null。
    private static int? ParseConfidence(string summary)
    {
        const string marker = "conf ";
        var i = summary.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var j = i + marker.Length;
        var k = j;
        while (k < summary.Length && char.IsDigit(summary[k])) k++;
        return k > j && int.TryParse(summary[j..k], out var n) ? n : null;
    }

    // 文字列から最初の URL（"://" を含む語）を取り出す。無ければ null。
    private static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return null;
        var start = scheme;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = scheme;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return text[start..end];
    }
}
