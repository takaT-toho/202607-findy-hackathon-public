using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SreAgent;

// エラーを識別する署名（ハッシュ）を計算する。重複処理の判定やブランチ名に使う。
// ポイント: JSON の「値」ではなく「構造（キーのパスと型）」だけをハッシュ対象にする。
// そのため delay=3 でも delay=10 でも、同じスキーマである限り同じ署名になり、重複としてまとめられる。
public static class ErrorSignature
{
    // `{ErrorType}:{16桁hex}` 形式の署名を返す。同じエラー種別＆同じ JSON 構造なら必ず同じ値になる。
    public static string Compute(ErrorPayload payload)
    {
        var shape = NormalizeShape(payload.RawJsonResponse);
        var material = $"{payload.ErrorType}\n{shape}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var hex = Convert.ToHexStringLower(bytes);
        return $"{payload.ErrorType}:{hex[..16]}";
    }

    // JSON を「キーのパス:型」の並び（値は無視）に正規化する。
    // JSON として読めなければ、空白を畳んだ生文字列を返す。
    public static string NormalizeShape(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var paths = new List<string>();
            Collect(doc.RootElement, "$", paths);
            paths.Sort(StringComparer.Ordinal);
            return string.Join("\n", paths);
        }
        catch (JsonException)
        {
            // JSON にならない応答（HTML エラーページ等）は、空白を畳んで丸ごと指紋にする。
            return string.Join(' ', rawJson.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    // 各要素を再帰的にたどり「パス:型」を集める。配列は先頭要素の構造だけを見る。
    private static void Collect(JsonElement el, string path, List<string> acc)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var hasMember = false;
                foreach (var prop in el.EnumerateObject())
                {
                    hasMember = true;
                    Collect(prop.Value, $"{path}.{prop.Name}", acc);
                }
                if (!hasMember)
                {
                    acc.Add($"{path}:object");
                }
                break;
            case JsonValueKind.Array:
                var first = el.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Undefined)
                {
                    acc.Add($"{path}[]:empty");
                }
                else
                {
                    Collect(first, $"{path}[]", acc);
                }
                break;
            case JsonValueKind.String:
                acc.Add($"{path}:string");
                break;
            case JsonValueKind.Number:
                acc.Add($"{path}:number");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                acc.Add($"{path}:bool");
                break;
            case JsonValueKind.Null:
                acc.Add($"{path}:null");
                break;
        }
    }
}
