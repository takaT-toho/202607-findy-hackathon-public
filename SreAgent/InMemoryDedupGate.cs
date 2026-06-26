using System.Collections.Concurrent;

namespace SreAgent;

// IDedupGate の既定実装。「エラー署名 → 最後に処理した時刻」をメモリに持ち、
// 一定時間（window）内に来た同じエラーを重複として弾く。再起動で記録は消える。
public class InMemoryDedupGate : IDedupGate
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;

    // now は現在時刻を返す関数（テストで時刻を差し替えられるようにしている）。
    public InMemoryDedupGate(TimeSpan window, Func<DateTimeOffset>? now = null)
    {
        _window = window;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // 前回処理から window 未満なら false（重複）。それ以外は処理時刻を更新して true。
    public bool TryAcquire(string signature)
    {
        var now = _now();
        var acquired = false;
        _seen.AddOrUpdate(
            signature,
            _ =>
            {
                acquired = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last >= _window)
                {
                    acquired = true;
                    return now;
                }
                acquired = false;
                return last; // 窓内なので時刻は更新しない
            });
        return acquired;
    }

    // 記録を消して、失敗時にすぐ再処理できるようにする。
    public void Release(string signature) => _seen.TryRemove(signature, out _);
}
