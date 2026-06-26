namespace SreAgent;

// 処理中のエラーケース ID を、非同期処理の流れに沿って引き回すための仕組み。
// AsyncLocal を使うので、並行する別リクエストの ID と混ざらない。
// これにより、ログ出力などが ID を引数で受け取らなくても自動で付与できる。
public static class CaseContext
{
    private static readonly AsyncLocal<string?> _current = new();

    // 現在の処理に紐づくケース ID。スコープ外では null。
    public static string? Current => _current.Value;

    // caseId をセットしたスコープを開始する。戻り値を Dispose すると元に戻る。
    public static IDisposable Begin(string caseId)
    {
        var previous = _current.Value;
        _current.Value = caseId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
