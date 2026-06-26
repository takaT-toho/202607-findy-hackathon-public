using CSharpFunctionalExtensions;

namespace SreAgent;

// AI が生成した修正コードを実際にビルド（必要ならテスト）して検証する。
// 「コンパイルできない・テストに通らない PR」を作らないためのゲート。
public interface IBuildVerifier
{
    // testCode があればビルド成功後に dotnet test も実行する。
    // 失敗は CompilationFailed / TestFailed / VerifierUnavailable のいずれかで返る。
    Task<Result<Unit, BuildError>> VerifyAsync(string filePath, string fixedFileContent, string? testCode = null);
}

// 「成功したが返す値はない」ことを表す単位型。
public readonly record struct Unit
{
    public static readonly Unit Value = new();
}
