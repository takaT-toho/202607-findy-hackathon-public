namespace SreAgent;

// 監視対象アプリから /webhook/error で受け取るエラー情報。
// （TargetApp 側の同名レコードと同じ形。共有はせず両側に複製している。）
public record ErrorPayload(
    string ErrorType,
    string ErrorMessage,
    string StackTrace,
    string RawJsonResponse,
    string ExpectedSchema,
    string FaultyMethodSource,
    string FilePath
);
