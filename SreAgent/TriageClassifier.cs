using System.Text.Json;
using Google.GenAI;
using Microsoft.Extensions.Options;

namespace SreAgent;

// 本番用の LLM 分類器。決定論ゲートで判別できなかった曖昧なエラーだけを Gemini に分類させる。
// 通信失敗・解釈不能・未知の分類などは安全側に倒して Unknown（人手対応）にする。
public class TriageClassifier : ITriageClassifier
{
    private readonly Client _client;
    private readonly GeminiSettings _settings;
    private readonly ILogger<TriageClassifier> _logger;

    public TriageClassifier(IOptions<GeminiSettings> settings, ILogger<TriageClassifier> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client = new Client(
            project: _settings.ProjectId,
            location: _settings.Region,
            enterprise: true);
    }

    public async Task<TriageVerdict> ClassifyAsync(ErrorPayload payload)
    {
        string responseText;
        try
        {
            var response = await _client.Models.GenerateContentAsync(
                model: _settings.ModelId,
                contents: BuildPrompt(payload),
                config: new Google.GenAI.Types.GenerateContentConfig { ResponseMimeType = "application/json" });
            responseText = response?.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Triage classification failed; escalating to human");
            return TriageVerdict.NotFixable(ErrorClass.Unknown,
                $"分類器の呼び出しに失敗したため人手に委ねます: {ex.Message}", confidence: 0);
        }

        return Parse(responseText);
    }

    // 分類器の JSON 応答を判定に変換する。解釈できない・未知の分類なら Unknown に丸める。
    private TriageVerdict Parse(string responseText)
    {
        var start = responseText.IndexOf('{');
        var end = responseText.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            _logger.LogWarning("Triage response had no JSON object: {Text}", responseText);
            return TriageVerdict.NotFixable(ErrorClass.Unknown, "分類器の応答を解釈できませんでした。", confidence: 0);
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
            var root = doc.RootElement;

            // LLM は型を外すことがあるので、各フィールドは ValueKind を確認してから取り出す。
            var classText = root.TryGetProperty("error_class", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()! : "(分類器が理由を返しませんでした)";
            var confidence = root.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number
                ? NormalizeConfidence(cf) : 0;

            if (!Enum.TryParse<ErrorClass>(classText, ignoreCase: true, out var cls))
            {
                _logger.LogWarning("Triage returned unknown error_class '{Class}'", classText);
                return TriageVerdict.NotFixable(ErrorClass.Unknown,
                    $"分類器が未知のクラス '{classText}' を返したため人手に委ねます。", confidence: 0);
            }

            return TriageVerdict.FromClass(cls, reason, confidence);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse triage JSON");
            return TriageVerdict.NotFixable(ErrorClass.Unknown, "分類器の JSON 解析に失敗しました。", confidence: 0);
        }
    }

    // confidence を 0〜100 の整数に正規化する。0〜1 で来た場合は 100 倍する。
    private static int NormalizeConfidence(JsonElement cf)
    {
        var value = cf.GetDouble();
        var scaled = value <= 1.0 ? value * 100 : value;
        return Math.Clamp((int)Math.Round(scaled), 0, 100);
    }

    private static string BuildPrompt(ErrorPayload payload) => $$"""
        あなたは C#/.NET のSREエージェントの分類器です。本番で起きたエラーを、機械修正の適否で分類してください。

        ## 分類カテゴリ（error_class はこの英字いずれか）
        - SchemaDrift: 外部APIのJSONスキーマ構造が変わったことによるデシリアライズ失敗。1メソッド内のマッピング修正で直る。
        - MappingFault: スキーマ構造は同じだが enum値・単位（秒↔分等）・日時/数値フォーマットの差で失敗。1メソッド内で直る。
        - TransientUpstream: 上流の一過性障害（5xx・エラー応答・不達）。コード修正では直らない。
        - NeedsHuman: 複数ファイルに波及する、または修正方針が曖昧でコード修正の確信が持てない。
        - Unknown: 上記のいずれとも判断できない。

        ## エラー情報
        エラー種別: {{payload.ErrorType}}
        エラーメッセージ: {{payload.ErrorMessage}}
        実際に受け取った応答（After）:
        {{payload.RawJsonResponse}}
        期待していたスキーマ（Before）:
        {{payload.ExpectedSchema}}
        対象メソッド:
        {{payload.FaultyMethodSource}}

        ## 出力形式（JSONのみ・説明文不要）
        {
          "error_class": "SchemaDrift",
          "reason": "なぜそう分類したかを1〜2文で",
          "confidence": 85
        }
        """;
}
