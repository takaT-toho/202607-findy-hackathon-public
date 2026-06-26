using System.Text.Json;

namespace SreAgent;

// 「明らかに AI で直すべきでないエラー」を決定論ルールで弾く誤検知ゲート。
// トリガーは JSON デシリアライズ失敗全般なので、スキーマ変更でない一過性障害
// （HTML エラーページ、{"error":...} 応答、通信エラー）も同じ経路に乗ってしまう。
// それらを LLM に渡す前に、無料で確実に判定できるルールで落とす。
// 判断できないものは null を返し、後段の LLM 分類器に委ねる（弾きすぎない）。
public static class FalsePositiveGate
{
    // エラー応答でよく使われるキー。top-level がこれら「だけ」なら envelope 候補とみなす。
    private static readonly HashSet<string> EnvelopeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "error", "errors", "message", "statuscode", "status", "code", "title", "detail", "traceid", "type", "instance"
    };

    // これらのキーがあれば「実際にエラーを表す応答」と判断する。
    private static readonly HashSet<string> EnvelopeErrorMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "error", "errors", "statuscode", "code", "title", "detail"
    };

    // 通信・タイムアウト系の例外型名。コード修正では直らない。
    private static readonly string[] TransientErrorTypes =
    {
        "HttpRequestException", "TaskCanceledException", "TimeoutException", "SocketException", "OperationCanceledException"
    };

    // 「AI で直すべきでない」と確信できれば判定を返す。判断できなければ null。
    public static TriageVerdict? Inspect(ErrorPayload payload)
    {
        // 判定1: 例外型が通信・タイムアウト系なら一過性障害。
        foreach (var t in TransientErrorTypes)
        {
            if (payload.ErrorType.Contains(t, StringComparison.OrdinalIgnoreCase))
            {
                return TriageVerdict.NotFixable(
                    ErrorClass.TransientUpstream,
                    $"例外型 {payload.ErrorType} は通信・タイムアウト系の一過性障害であり、コード修正では解消しません。",
                    confidence: 95);
            }
        }

        var raw = payload.RawJsonResponse;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 応答本文が無い＝スキーマ変更の証拠がない。一過性寄りと判断する。
            return TriageVerdict.NotFixable(
                ErrorClass.TransientUpstream,
                "応答本文が空です。スキーマ変更の証拠がなく、上流の一過性障害の可能性が高いです。",
                confidence: 80);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            // 判定2: そもそも JSON として読めない（HTML エラーページ等）。
            return TriageVerdict.NotFixable(
                ErrorClass.TransientUpstream,
                "応答が JSON として解釈できません（HTMLエラーページ等）。スキーマ変更ではなく上流障害の可能性が高いです。",
                confidence: 90);
        }

        using (doc)
        {
            // 判定3: top-level がエラー応答のキーだけで、業務データを含まない。
            if (LooksLikeErrorEnvelope(doc.RootElement))
            {
                return TriageVerdict.NotFixable(
                    ErrorClass.TransientUpstream,
                    "応答が error envelope（error/message/statusCode 等のみ）で、業務データを含みません。上流のエラー応答と判断します。",
                    confidence: 85);
            }
        }

        // どれにも当てはまらなければ判断保留。
        return null;
    }

    // top-level が「エラー応答のキーだけ」で構成されているかを判定する。
    // 業務データのキーが 1 つでも混ざっていれば false（エラー応答とは断定しない）。
    private static bool LooksLikeErrorEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasErrorMarker = false;
        var hasAnyProperty = false;
        foreach (var prop in root.EnumerateObject())
        {
            hasAnyProperty = true;
            if (!EnvelopeKeys.Contains(prop.Name))
            {
                return false;
            }
            if (EnvelopeErrorMarkers.Contains(prop.Name))
            {
                hasErrorMarker = true;
            }
        }

        return hasAnyProperty && hasErrorMarker;
    }
}
