using System.Security.Cryptography;
using System.Text;

namespace TargetApp;

// 送信本文を HMAC-SHA256 で署名する。SRE Agent 側が同じ秘密鍵で検証する。
// （SRE Agent の WebhookSignature と同じ実装を複製している。）
public static class WebhookSignature
{
    // "sha256=<小文字hex>" 形式の署名を返す。
    public static string Compute(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
