namespace TargetApp;

// 例外から、SRE Agent に送る ErrorPayload（生 JSON・期待スキーマ・対象メソッドのソース）を組み立てるヘルパー。
public static class ErrorPayloadBuilder
{
    // filePath はリポジトリ相対の正しいパスを渡す（AI に推測させないための権威ある値）。
    public static ErrorPayload Build(TransitJsonException ex, string methodName, string filePath)
    {
        return new ErrorPayload(
            ErrorType: nameof(System.Text.Json.JsonException),
            ErrorMessage: ex.Message,
            StackTrace: ex.StackTrace ?? string.Empty,
            RawJsonResponse: ex.RawJson,
            ExpectedSchema: SourceReader.GetRecordDefinition<TrainStatus>(),
            FaultyMethodSource: SourceReader.Read(methodName),
            FilePath: filePath
        );
    }
}
