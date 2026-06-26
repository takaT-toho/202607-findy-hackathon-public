namespace SreAgent;

// 進まず詰まっているケースを定期的にスキャンし、自動で人間にエスカレーションする常駐サービス。
// どこかの処理がハングしてもケースを放置せず、終端（Escalated）へ移してダッシュボードに目立たせる。
public sealed class StuckMonitor : BackgroundService
{
    private readonly IEventLog _eventLog;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _threshold;
    private readonly ILogger<StuckMonitor> _logger;

    public StuckMonitor(IEventLog eventLog, TimeSpan interval, TimeSpan threshold, ILogger<StuckMonitor> logger)
    {
        _eventLog = eventLog;
        _interval = interval;
        _threshold = threshold;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // 1 回のスキャンが失敗しても監視全体は止めない。
                try
                {
                    await ScanOnceAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stuck scan failed; continuing");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    // 1 回分のスキャン（テストしやすいよう時刻を引数で渡せる）。
    // 詰まっているケースに Escalated を 1 件ずつ記録し、その件数を返す。
    public async Task<int> ScanOnceAsync(DateTimeOffset? now = null)
    {
        var when = now ?? DateTimeOffset.UtcNow;
        var events = await _eventLog.GetRecentAsync(int.MaxValue);
        var cases = CaseProjection.Fold(events);
        var stuck = StuckPolicy.FindStuck(cases, when, _threshold);

        foreach (var c in stuck)
        {
            // 正しい CaseId でイベントが記録されるよう、コンテキストにケース ID を載せる。
            using (CaseContext.Begin(c.CaseId))
            {
                await _eventLog.RecordAsync(EventType.Escalated,
                    $"Auto-escalated (stuck > {_threshold.TotalMinutes:0}m)",
                    $"{c.State} のまま {(when - c.LastUpdated).TotalMinutes:0} 分更新がないため、監視が自動エスカレーションしました。");
            }
            _logger.LogWarning("Auto-escalated stuck case {CaseId} (state {State})", c.CaseId, c.State);
        }

        return stuck.Count;
    }
}
