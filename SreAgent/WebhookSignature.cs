using System.Security.Cryptography;
using System.Text;

namespace SreAgent;

// Webhook の HMAC-SHA256 署名を計算・検証する。
// 共有秘密で署名された通知だけを受理し、第三者が勝手に POST して AI 課金や PR 作成を誘発するのを防ぐ。
public static class WebhookSignature
{
    // 本文から "sha256=<小文字hex>" 形式の署名を作る。
    public static string Compute(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    // 署名が正しければ true。secret 未設定なら検証せず true（ローカル開発用）。
    public static bool Verify(string? secret, string body, string? provided)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return true;
        }
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }
        var expected = Compute(secret, body);
        // タイミング攻撃を避けるため、長さが違っても一定時間で比較する。
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
    }
}
