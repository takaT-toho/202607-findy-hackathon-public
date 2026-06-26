using System.Text.Json;
using CSharpFunctionalExtensions;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;

namespace SreAgent;

// Gemini に修正案を生成させる本番実装（IAiCore）。
// Google Cloud の認証は ADC（Application Default Credentials）に任せる。
public class AiCore : IAiCore
{
    private readonly Client _client;
    private readonly GeminiSettings _settings;
    private readonly ILogger<AiCore> _logger;

    public AiCore(IOptions<GeminiSettings> settings, ILogger<AiCore> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client = new Client(
            project: _settings.ProjectId,
            location: _settings.Region,
            enterprise: true);
    }

    // エラー情報から修正案（AiOutput）を生成する。
    // 例外は投げず、失敗はすべて AiError 派生型として Result.Failure で返す。
    // previousAttemptErrors にはリトライ時に直前の試行で出たエラーを渡す。
    public async Task<Result<AiOutput, AiError>> GenerateFixAsync(
        ErrorPayload payload, string? previousAttemptErrors = null, ErrorClass errorClass = ErrorClass.SchemaDrift)
    {
        var prompt = BuildPrompt(payload, previousAttemptErrors, errorClass);
        var config = new GenerateContentConfig
        {
            // JSON 形式で返すよう指示する（具体的な構造はプロンプト側で指定している）。
            ResponseMimeType = "application/json"
        };

        string responseText;
        try
        {
            var response = await _client.Models.GenerateContentAsync(
                model: _settings.ModelId,
                contents: prompt,
                config: config);
            responseText = response?.Text ?? string.Empty;
        }
        catch (ClientError ex)
        {
            _logger.LogError(ex, "Gemini ClientError");
            return Result.Failure<AiOutput, AiError>(new ApiQuotaExceededError(ex.Message));
        }
        catch (ServerError ex)
        {
            _logger.LogError(ex, "Gemini ServerError");
            return Result.Failure<AiOutput, AiError>(new NetworkError(ex.Message));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini network error");
            return Result.Failure<AiOutput, AiError>(new NetworkError(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Gemini error");
            return Result.Failure<AiOutput, AiError>(new NetworkError(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Result.Failure<AiOutput, AiError>(new SchemaMismatchError("Empty response from Gemini"));
        }

        // 応答の前後に余計なテキストが付くことがあるため、JSON 部分だけを抜き出す。
        var jsonText = ExtractJsonObject(responseText);
        if (jsonText is null)
        {
            return Result.Failure<AiOutput, AiError>(
                new SchemaMismatchError($"No JSON object found in response: {Truncate(responseText, 200)}"));
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var prDescription = GetString(root, "pr_description");
            var filePath = GetString(root, "file_path");
            var fixedCode = GetString(root, "fixed_method_code");
            var impact = GetString(root, "impact_analysis");
            var testCode = GetString(root, "test_code");
            var confidence = GetConfidence(root);

            if (string.IsNullOrWhiteSpace(prDescription) || string.IsNullOrWhiteSpace(fixedCode))
            {
                return Result.Failure<AiOutput, AiError>(
                    new SchemaMismatchError("AI output missing required fields (pr_description / fixed_method_code)"));
            }

            // 後段のコード置換は START / END マーカーで対象範囲を特定する。両方揃っていなければ弾く。
            if (!fixedCode.Contains("[AGENT-MANAGED-START", StringComparison.Ordinal)
                || !fixedCode.Contains("[AGENT-MANAGED-END", StringComparison.Ordinal))
            {
                return Result.Failure<AiOutput, AiError>(
                    new MarkerMissingError("fixed_method_code must contain both [AGENT-MANAGED-START: X] and [AGENT-MANAGED-END: X] markers"));
            }

            var output = new AiOutput(prDescription, filePath, fixedCode, confidence, impact, testCode);
            return Result.Success<AiOutput, AiError>(output);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini JSON: {Text}", Truncate(jsonText, 500));
            return Result.Failure<AiOutput, AiError>(new SchemaMismatchError($"JSON parse failed: {ex.Message}"));
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : null;
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    // confidence は 0〜100 で扱う。AI が 0.0〜1.0 で返してきた場合は 100 倍して正規化する。
    private static int GetConfidence(JsonElement root)
    {
        if (!root.TryGetProperty("confidence", out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }
        var value = el.GetDouble();
        return value <= 1.0 ? (int)Math.Round(value * 100) : (int)Math.Round(value);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...(truncated)";

    // Gemini に渡すプロンプトを組み立てる。エラー情報・制約・期待する JSON 出力形式を指示する。
    private static string BuildPrompt(ErrorPayload payload, string? previousAttemptErrors, ErrorClass errorClass) => $$"""
        あなたはC#/.NET 10のエキスパートエンジニアです。
        {{BuildProblemStatement(errorClass)}}
        以下の情報をもとに、エラーが発生しているメソッドを修正してください。
        {{BuildRetrySection(previousAttemptErrors)}}

        ## エラー情報
        エラー種別: {{payload.ErrorType}}
        エラーメッセージ: {{payload.ErrorMessage}}
        スタックトレース:
        {{payload.StackTrace}}

        ## 実際に受け取ったJSONレスポンス（新スキーマ / After）
        {{payload.RawJsonResponse}}

        ## 期待していたスキーマ定義（旧スキーマ / Before）
        {{payload.ExpectedSchema}}

        ## 修正が必要なメソッドのソースコード
        {{payload.FaultyMethodSource}}

        ## 制約
        - 修正はメソッド内部のロジックのみを変更すること
        - メソッドシグネチャ（名前・引数・戻り値型）は変更しないこと
        - 新しい record 型が必要な場合はメソッドの直前に定義すること
        - using 文は追加しないこと（既存のものを前提とする）
        - 上記「修正が必要なメソッドのソースコード」に実在する変数・フィールド
          （例: _http, _logger）と型（例: TrainStatus）だけを使うこと。新しい変数名を発明しない

        ## fixed_method_code のマーカー（最重要・厳守）
        fixed_method_code は必ず次の形式の文字列にすること:

        // [AGENT-MANAGED-START: メソッド名]
        （修正後のメソッド全体）
        // [AGENT-MANAGED-END: メソッド名]

        - 先頭の `// `（スラッシュ2つ + 半角スペース）を**必ず**付ける
        - START 行と END 行の**両方**を必ず含める（END を省略しない）
        - 「メソッド名」は上記ソースコードのマーカーと完全に同一にする
        - 元のソースコードのインデント（半角スペース4つ）を維持する

        ## confidence の付け方（0〜100 の整数。小数や 0〜1 のスケールは使わない）
        - 100: 元スキーマと新スキーマの差分が明確で、変換ロジックに曖昧さがない
        - 75: 差分は明確だが、フィールド名や型の解釈に1つ以上の判断が含まれる
        - 50: 新スキーマに型情報の手がかりが少なく、推測を含む
        - 25 以下: 修正方針が複数考えられ、どれが正解か判断しがたい

        ## impact_analysis の書き方
        - このメソッドを呼び出している箇所への影響
        - 戻り値の型やフィールドの意味が変わる場合の注意点
        - 既存のデータ・キャッシュ・テストへの影響
        - 2〜4 文で簡潔に

        ## test_code（修正の妥当性を検証する xUnit テスト・必須）
        修正が正しいことを `dotnet test` で機械検証できるよう、xUnit テストファイル**全体**を出力すること。
        - HTTP 通信を伴うメソッド全体ではなく、「**新スキーマの生 JSON を、修正後と同じロジックで読み、
          期待値にマッピングできること**」を検証する単体テストにする（外部通信に依存させない）。
        - 対象アプリの型（例: `TargetApp.TrainStatus`）は `using TargetApp;` で参照してよい。
        - `using System.Text.Json;` と `using Xunit;` を含め、1つ以上の `[Fact]` を書く。
        - テストクラスは名前空間なしのトップレベルでよい。`Assert.Equal` 等で新スキーマの代表値を検証する。
        - 上記「実際に受け取ったJSONレスポンス（新スキーマ）」を埋め込み JSON として使う。

        ## 出力形式
        必ず以下の JSON フォーマットのみで回答すること（説明文・マークダウン不要）。
        confidence は整数（例: 85）。fixed_method_code と test_code は改行を含む文字列:
        {
          "pr_description": "string",
          "file_path": "string",
          "fixed_method_code": "string",
          "confidence": 85,
          "impact_analysis": "string",
          "test_code": "string"
        }
        """;

    // エラー分類ごとに「何が起きたか」の説明文を切り替える。
    private static string BuildProblemStatement(ErrorClass errorClass) => errorClass switch
    {
        ErrorClass.MappingFault =>
            "外部APIのスキーマ構造は同じですが、値の型・単位（秒↔分等）・enum値・日時/数値フォーマットの差異により、"
            + "デシリアライズまたは値のマッピングに失敗しました。受信値を正しい型・単位へ変換してください。",
        _ =>
            "外部APIのJSONスキーマ変更によりデシリアライズエラーが発生しました。",
    };

    // リトライ時のみ、直前のエラーを示して「同じ失敗を繰り返さない」よう促すセクションを足す。
    private static string BuildRetrySection(string? previousAttemptErrors)
    {
        if (string.IsNullOrWhiteSpace(previousAttemptErrors))
        {
            return string.Empty;
        }
        return $$"""

            ## 直前の試行は失敗しました（必ず直すこと）
            前回の修正案は build または test で以下のエラーになりました。
            同じ誤りを繰り返さず、これらを解消する修正と test_code を出力してください:
            ```
            {{previousAttemptErrors}}
            ```
            """;
    }
}
