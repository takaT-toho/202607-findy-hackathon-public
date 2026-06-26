using CSharpFunctionalExtensions;

namespace SreAgent;

// 既定のビルド検証。何もせず常に成功を返す（検証をスキップしたいときに使う）。
// 実際にビルドして検証するのは DotnetBuildVerifier。
public class NoOpBuildVerifier : IBuildVerifier
{
    public Task<Result<Unit, BuildError>> VerifyAsync(string filePath, string fixedFileContent, string? testCode = null)
        => Task.FromResult(Result.Success<Unit, BuildError>(Unit.Value));
}
