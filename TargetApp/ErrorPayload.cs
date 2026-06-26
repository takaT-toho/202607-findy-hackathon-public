namespace TargetApp;

// SRE Agent へエラーを通知するときに送るデータ。
// （SRE Agent 側にも同じ形の record があるが、共有はせず両側に複製している。）
public record ErrorPayload(
    string ErrorType,
    string ErrorMessage,
    string StackTrace,
    string RawJsonResponse,
    string ExpectedSchema,
    string FaultyMethodSource,
    string FilePath
);
