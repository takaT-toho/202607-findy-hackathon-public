using CSharpFunctionalExtensions;

namespace SreAgent;

// エラー情報から修正案を生成する。開発環境では StubAiCore、本番では AiCore（Gemini）が使われる。
// 失敗は例外ではなく Result で返す。
public interface IAiCore
{
    // previousAttemptErrors: リトライ時に直前の試行で出たエラー。初回は null。
    // errorClass: Triage が確定したエラー分類。プロンプトの切り替えに使う。
    Task<Result<AiOutput, AiError>> GenerateFixAsync(
        ErrorPayload payload,
        string? previousAttemptErrors = null,
        ErrorClass errorClass = ErrorClass.SchemaDrift);
}
