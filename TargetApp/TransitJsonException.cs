using System.Text.Json;

namespace TargetApp;

// JSON のデシリアライズに失敗したときに投げる例外。
// 失敗した生の JSON 文字列（RawJson）を保持し、エラー通知に使う。
public class TransitJsonException : JsonException
{
    public string RawJson { get; }

    public TransitJsonException(string rawJson, JsonException inner)
        : base(inner.Message, inner)
    {
        RawJson = rawJson;
    }
}
