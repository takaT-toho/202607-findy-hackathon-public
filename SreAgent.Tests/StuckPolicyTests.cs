using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// 詰まり判定のテスト: 終端でなく閾値を超えたものだけが「詰まっている」。終端は常に詰まり扱いしない。
public class StuckPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(CaseState.Fixing)]
    [InlineData(CaseState.Reviewing)]
    [InlineData(CaseState.Triaged)]
    [InlineData(CaseState.Verified)]
    [InlineData(CaseState.Detected)]
    public void Nonterminal_past_threshold_is_stuck(CaseState state)
    {
        var lastUpdated = Now - TimeSpan.FromMinutes(11);
        Assert.True(StuckPolicy.IsStuck(state, lastUpdated, Now, Threshold));
    }

    [Fact]
    public void Within_threshold_is_not_stuck()
    {
        var lastUpdated = Now - TimeSpan.FromMinutes(9);
        Assert.False(StuckPolicy.IsStuck(CaseState.Fixing, lastUpdated, Now, Threshold));
    }

    [Theory]
    [InlineData(CaseState.PrOpened)]
    [InlineData(CaseState.Failed)]
    [InlineData(CaseState.Rejected)]
    [InlineData(CaseState.Escalated)]
    public void Terminal_states_are_never_stuck(CaseState state)
    {
        var lastUpdated = Now - TimeSpan.FromHours(5); // どれだけ古くても終端なら stuck ではない
        Assert.False(StuckPolicy.IsStuck(state, lastUpdated, Now, Threshold));
    }

    [Fact]
    public void FindStuck_filters_only_stuck_cases()
    {
        var stuck = new CaseView("A", null, CaseState.Fixing, 1, null, null,
            Now - TimeSpan.FromMinutes(20), Now - TimeSpan.FromMinutes(20), Array.Empty<AgentEvent>());
        var fresh = new CaseView("B", null, CaseState.Fixing, 1, null, null,
            Now - TimeSpan.FromMinutes(1), Now - TimeSpan.FromMinutes(1), Array.Empty<AgentEvent>());
        var done = new CaseView("C", null, CaseState.PrOpened, 1, null, null,
            Now - TimeSpan.FromHours(2), Now - TimeSpan.FromHours(2), Array.Empty<AgentEvent>());

        var result = StuckPolicy.FindStuck(new[] { stuck, fresh, done }, Now, Threshold);

        Assert.Equal(new[] { "A" }, result.Select(c => c.CaseId));
    }
}
