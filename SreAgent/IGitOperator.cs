using CSharpFunctionalExtensions;

namespace SreAgent;

// AI が作った修正コードから Git ブランチを切って PR を作成する。
// 開発環境では StubGitOperator、本番では GitOperator が使われる。
// 失敗は例外ではなく Result で返す。
public interface IGitOperator
{
    Task<Result<string, GitOperatorError>> CreatePrAsync(PrContext ctx);
}
