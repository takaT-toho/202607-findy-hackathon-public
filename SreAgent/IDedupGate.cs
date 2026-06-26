namespace SreAgent;

// 同じエラーが短時間に繰り返し届いたとき、重複処理を防ぐゲート。
// コストの高い AI 呼び出しや PR 作成の手前で使う。
public interface IDedupGate
{
    // 処理してよければ true を返し処理時刻を記録する。一定時間内に同じものを処理済みなら false。
    bool TryAcquire(string signature);

    // 処理が失敗したときに記録を取り消し、すぐ再処理できるようにする。
    void Release(string signature);
}
