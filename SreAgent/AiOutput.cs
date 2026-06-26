using System.Text.Json.Serialization;

namespace SreAgent;

// AI が返す修正結果。JSON のキー名と対応づけている。
// TestCode は修正の妥当性を検証する xUnit テスト全体（空なら検証はスキップ）。
public record AiOutput(
    [property: JsonPropertyName("pr_description")] string PrDescription,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("fixed_method_code")] string FixedMethodCode,
    [property: JsonPropertyName("confidence")] int Confidence,
    [property: JsonPropertyName("impact_analysis")] string ImpactAnalysis,
    [property: JsonPropertyName("test_code")] string TestCode = ""
);
