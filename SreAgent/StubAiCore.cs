using CSharpFunctionalExtensions;

namespace SreAgent;

// 開発環境用の AI。Gemini を呼ばず、あらかじめ用意した固定の修正結果を返す。
// AI の出力が毎回変わらないので、下流（PR 作成）のテストに集中できる。
public class StubAiCore : IAiCore
{
    private const string FixedMethodCode = """
        // [AGENT-MANAGED-START: FetchStatusAsync]
        public async Task<TrainStatus> FetchStatusAsync()
        {
            var raw = await _http.GetStringAsync("/api/transit/status");
            _logger.LogInformation("Fetched {Length} bytes from transit API", raw.Length);
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                return new TrainStatus
                {
                    LineId = root.GetProperty("line_id").GetString()!,
                    LineName = root.GetProperty("line_name").GetString()!,
                    Status = root.GetProperty("status").GetString()!,
                    DelayMinutes = root.GetProperty("delays").GetProperty("value").GetInt32(),
                    LastUpdated = root.GetProperty("last_updated").GetDateTimeOffset(),
                };
            }
            catch (JsonException ex)
            {
                throw new TransitJsonException(raw, ex);
            }
        }
        // [AGENT-MANAGED-END: FetchStatusAsync]
        """;

    // 修正が正しいことを確認する xUnit テスト（固定）。新スキーマ（delays.value）を読めるか検証する。
    private const string TestCode = """""
        using System.Text.Json;
        using TargetApp;
        using Xunit;

        public class FetchStatusAsyncSchemaTests
        {
            private const string NewSchemaJson = """"
                {
                  "line_id": "JR-001",
                  "line_name": "Yamanote",
                  "status": "delayed",
                  "delays": { "value": 7, "unit": "minutes" },
                  "last_updated": "2026-05-30T10:00:00+09:00"
                }
                """";

            [Fact]
            public void Maps_new_delays_object_to_TrainStatus()
            {
                using var doc = JsonDocument.Parse(NewSchemaJson);
                var root = doc.RootElement;
                var status = new TrainStatus
                {
                    LineId = root.GetProperty("line_id").GetString()!,
                    LineName = root.GetProperty("line_name").GetString()!,
                    Status = root.GetProperty("status").GetString()!,
                    DelayMinutes = root.GetProperty("delays").GetProperty("value").GetInt32(),
                    LastUpdated = root.GetProperty("last_updated").GetDateTimeOffset(),
                };
                Assert.Equal("JR-001", status.LineId);
                Assert.Equal(7, status.DelayMinutes);
            }
        }
        """"";

    // 引数は無視して、常に固定の修正結果を返す。
    public Task<Result<AiOutput, AiError>> GenerateFixAsync(
        ErrorPayload payload, string? previousAttemptErrors = null, ErrorClass errorClass = ErrorClass.SchemaDrift)
    {
        var output = new AiOutput(
            PrDescription: "[STUB] Fix delay_minutes -> delays.value mapping for new API schema",
            FilePath: "TargetApp/TransitService.cs",
            FixedMethodCode: FixedMethodCode,
            Confidence: 95,
            ImpactAnalysis: "FetchStatusAsync の戻り値型 TrainStatus は変わらず、内部の JSON マッピングのみを更新する。呼び出し元 (Program.cs の /trigger-fetch) には影響なし。既存のテスト・キャッシュにも互換性あり。",
            TestCode: TestCode
        );
        return Task.FromResult(Result.Success<AiOutput, AiError>(output));
    }
}
