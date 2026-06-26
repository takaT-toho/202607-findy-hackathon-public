using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// ブランチ名が決定論的（同じ入力なら同じ名前）であることを確認する。重複 PR 防止の前提になる。
public class GenerateBranchNameTests
{
    [Fact]
    public void Is_deterministic_for_same_inputs()
    {
        var a = GitOperator.GenerateBranchName("FetchStatusAsync", "JsonException:abcdef0123456789");
        var b = GitOperator.GenerateBranchName("FetchStatusAsync", "JsonException:abcdef0123456789");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Has_expected_prefix_and_no_timestamp()
    {
        var name = GitOperator.GenerateBranchName("FetchStatusAsync", "JsonException:abcdef0123456789");
        Assert.StartsWith("auto-fix/FetchStatusAsync-", name);
        // タイムスタンプ（14桁の数字）を含まない＝毎回変わる要素がないこと。
        Assert.DoesNotMatch(@"\d{14}", name);
    }

    [Fact]
    public void Different_signature_yields_different_branch()
    {
        var a = GitOperator.GenerateBranchName("FetchStatusAsync", "JsonException:1111111111111111");
        var b = GitOperator.GenerateBranchName("FetchStatusAsync", "JsonException:2222222222222222");
        Assert.NotEqual(a, b);
    }
}
