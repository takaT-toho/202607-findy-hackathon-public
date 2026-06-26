using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// マーカーで囲まれた範囲だけを置き換える処理のテスト。
public class ReplaceMethodTests
{
    private const string File = """
        before text
        // [AGENT-MANAGED-START: Foo]
        old body
        // [AGENT-MANAGED-END: Foo]
        after text
        """;

    [Fact]
    public void Replaces_only_between_markers()
    {
        var newCode = """
            // [AGENT-MANAGED-START: Foo]
            new body
            // [AGENT-MANAGED-END: Foo]
            """;

        var result = GitOperator.ReplaceMethod(File, "Foo", newCode);

        Assert.Contains("new body", result);
        Assert.DoesNotContain("old body", result);
        // マーカー外は不変。
        Assert.StartsWith("before text", result);
        Assert.EndsWith("after text", result);
    }

    [Fact]
    public void Throws_when_marker_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GitOperator.ReplaceMethod(File, "DoesNotExist", "x"));
        Assert.Contains("Marker", ex.Message);
    }

    [Fact]
    public void ExtractMethodName_reads_marker()
    {
        var name = GitOperator.ExtractMethodName("// [AGENT-MANAGED-START: FetchStatusAsync]\n...");
        Assert.Equal("FetchStatusAsync", name);
    }

    [Fact]
    public void ExtractMethodName_returns_null_without_marker()
    {
        Assert.Null(GitOperator.ExtractMethodName("no marker here"));
    }
}
