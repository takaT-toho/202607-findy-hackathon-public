namespace SreAgent;

// SemaphoreSlim を使った IConcurrencyGate 実装。同時実行数を maxConcurrent に制限する。
public sealed class SemaphoreConcurrencyGate : IConcurrencyGate, IDisposable
{
    private readonly SemaphoreSlim _sem;

    public SemaphoreConcurrencyGate(int maxConcurrent)
    {
        // 0 以下が来ても最低 1 本は通す。
        var n = maxConcurrent < 1 ? 1 : maxConcurrent;
        _sem = new SemaphoreSlim(n, n);
    }

    public int AvailableSlots => _sem.CurrentCount;

    public Task<bool> TryAcquireAsync(TimeSpan wait) => _sem.WaitAsync(wait);

    public void Release() => _sem.Release();

    public void Dispose() => _sem.Dispose();
}
