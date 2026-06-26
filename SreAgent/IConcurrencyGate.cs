namespace SreAgent;

// 同時に走る修正処理の本数に上限を設けるゲート。
// LLM の同時呼び出しを抑えて、コストとレート制限を守る。
public interface IConcurrencyGate
{
    // 空きがあれば（または wait の間に空けば）スロットを確保して true。
    // true を返したら必ず Release を 1 回呼ぶこと。
    Task<bool> TryAcquireAsync(TimeSpan wait);

    // 確保したスロットを返す。
    void Release();

    // 現在の空きスロット数（観測用）。
    int AvailableSlots { get; }
}
