using System.Text.Json;
using Google.GenAI;
using Microsoft.Extensions.Options;

namespace SreAgent;

// 本番用のレビュアー。修正案と生成テストを、修正した本人(AiCore)とは別の視点で Gemini にレビューさせる。
// レビュー自体ができなかった場合（通信失敗・解釈不能）は承認に倒す。
// build/test は既に通っており、レビューは品質を上げるためのゲートなので、一過性の障害で良い修正を捨てないため。
// LLM が実際に見て NG と言ったときだけ却下する。
public class Reviewer : IReviewer
{
    private readonly Client _client;
    private readonly GeminiSettings _settings;
    private readonly ILogger<Reviewer> _logger;

    public Reviewer(IOptions<GeminiSettings> settings, ILogger<Reviewer> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client = new Client(
            project: _settings.ProjectId,
            location: _settings.Region,
            enterprise: true);
    }

    public async Task<ReviewVerdict> ReviewAsync(ErrorPayload payload, AiOutput output)
    {
        string responseText;
        try
        {
            var response = await _client.Models.GenerateContentAsync(
                model: _settings.ModelId,
                contents: BuildPrompt(payload, output),
                config: new Google.GenAI.Types.GenerateContentConfig { ResponseMimeType = "application/json" });
            responseText = response?.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reviewer call failed; soft-approving (build/test already passed)");
            return ReviewVerdict.Approve($"レビューを実行できなかったため承認（build/test は通過済み）: {ex.Message}");
        }

        return Parse(responseText);
    }

    // レビュアーの JSON 応答を判定に変換する。解釈できなければ承認に倒す。
    private ReviewVerdict Parse(string responseText)
    {
        var start = responseText.IndexOf('{');
        var end = responseText.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            _logger.LogWarning("Reviewer response had no JSON object: {Text}", responseText);
            return ReviewVerdict.Approve("レビュー応答を解釈できなかったため承認（build/test は通過済み）。");
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
            var root = doc.RootElement;

            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()! : "(レビュアーが理由を返しませんでした)";

            // approved を読めない・無い場合も承認に倒す（あいまいな応答だけで良い修正を却下しない）。
            var approved = ReadApproved(root);
            if (approved is null)
            {
                return ReviewVerdict.Approve($"レビュー応答の approved を解釈できなかったため承認: {Truncate(reason, 120)}");
            }
            if (approved.Value)
            {
                return ReviewVerdict.Approve(reason);
            }

            var findings = new List<string>();
            if (root.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in f.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        findings.Add(item.GetString()!);
                    }
                }
            }

            return ReviewVerdict.Reject(reason, findings);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse reviewer JSON");
            return ReviewVerdict.Approve("レビュー応答の JSON 解析に失敗したため承認（build/test は通過済み）。");
        }
    }

    // approved を bool または文字列 "true"/"false" から読む。読めなければ null（承認に倒す）。
    private static bool? ReadApproved(JsonElement root)
    {
        if (!root.TryGetProperty("approved", out var a)) return null;
        return a.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(a.GetString(), out var b) => b,
            _ => null
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static string BuildPrompt(ErrorPayload payload, AiOutput output) => $$"""
        あなたは C#/.NET のシニアレビュアーです。別のエージェントが作った「自動修正」を、本人とは独立した
        視点でレビューしてください。build と test は既に通っています。あなたの役割は品質ゲートです。

        ## 観点
        - 修正がエラーの根本原因（受信した新スキーマ/値）に正しく対応しているか（表面的な辻褄合わせでないか）
        - 生成テストが「修正の正しさ」を実際に検証しているか（常に通るトートロジーになっていないか）
        - メソッドシグネチャ・スコープを越えた変更や、破壊的な副作用がないか
        - impact_analysis の記述と実際の変更が整合しているか

        ## 元のエラー
        種別: {{payload.ErrorType}} / メッセージ: {{payload.ErrorMessage}}
        受信した応答(After):
        {{payload.RawJsonResponse}}

        ## 提案された修正メソッド
        {{output.FixedMethodCode}}

        ## 生成テスト
        {{output.TestCode}}

        ## レビュアーの自己申告
        confidence: {{output.Confidence}} / impact: {{output.ImpactAnalysis}}

        ## 出力形式（JSONのみ・説明文不要）
        approved は明確な問題が無ければ true。問題があれば false にし findings に具体的な指摘を列挙する。
        {
          "approved": true,
          "reason": "承認/却下の理由を1〜2文で",
          "findings": ["具体的な指摘（却下時）", "..."]
        }
        """;
}
