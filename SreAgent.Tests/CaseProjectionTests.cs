using SreAgent;
using Xunit;

namespace SreAgent.Tests;

// CaseProjection のテスト: イベント列から各ケースの状態を正しく導き、順序に依存せず・状態が巻き戻らないこと。
public class CaseProjectionTests
{
    private static AgentEvent Ev(EventType type, string caseId, int tsSeconds, string summary = "s", string? detail = null) =>
        new(new DateTimeOffset(2026, 5, 31, 0, 0, tsSeconds, TimeSpan.Zero), type, summary, detail, caseId);

    [Fact]
    public void Happy_path_ends_in_PrOpened()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "C1", 0),
            Ev(EventType.Triaged, "C1", 1, "Classified as SchemaDrift (fixable=True, conf 90)"),
            Ev(EventType.FixAttemptStarted, "C1", 2),
            Ev(EventType.TestVerifyCompleted, "C1", 3),
            Ev(EventType.PrCreated, "C1", 4, "PR created: https://github.com/x/y/pull/1"),
        };

        var c = Assert.Single(CaseProjection.Fold(events));
        Assert.Equal(CaseState.PrOpened, c.State);
        Assert.Equal(ErrorClass.SchemaDrift, c.Class);
        Assert.Equal(90, c.TriageConfidence);
        Assert.Equal(1, c.FixAttempts);
        Assert.Equal("https://github.com/x/y/pull/1", c.PrUrl);
    }

    [Fact]
    public void Transient_rejection_ends_in_Rejected()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "C2", 0),
            Ev(EventType.Triaged, "C2", 1, "Classified as TransientUpstream (fixable=False, conf 90)"),
            Ev(EventType.TriageRejected, "C2", 2),
        };

        var c = Assert.Single(CaseProjection.Fold(events));
        Assert.Equal(CaseState.Rejected, c.State);
        Assert.Equal(ErrorClass.TransientUpstream, c.Class);
        Assert.Equal(0, c.FixAttempts);
    }

    [Fact]
    public void Pr_url_is_extracted_for_file_and_https_schemes()
    {
        var fileCase = CaseProjection.Fold(new List<AgentEvent>
        {
            Ev(EventType.PrCreated, "F", 0, "PR: file:///D:/repo/out/x.cs"),
        }).Single();
        Assert.Equal("file:///D:/repo/out/x.cs", fileCase.PrUrl);
    }

    [Fact]
    public void Escalation_ends_in_Escalated()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "C3", 0),
            Ev(EventType.Triaged, "C3", 1, "Classified as NeedsHuman (fixable=False, conf 40)"),
            Ev(EventType.Escalated, "C3", 2),
        };

        Assert.Equal(CaseState.Escalated, Assert.Single(CaseProjection.Fold(events)).State);
    }

    [Fact]
    public void Exhausted_attempts_end_in_Failed()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "C4", 0),
            Ev(EventType.Triaged, "C4", 1, "Classified as SchemaDrift (fixable=True, conf 80)"),
            Ev(EventType.FixAttemptStarted, "C4", 2),
            Ev(EventType.FixAttemptStarted, "C4", 3),
            Ev(EventType.PrFailed, "C4", 4),
        };

        var c = Assert.Single(CaseProjection.Fold(events));
        Assert.Equal(CaseState.Failed, c.State);
        Assert.Equal(2, c.FixAttempts);
    }

    [Fact]
    public void Success_is_not_regressed_by_a_later_duplicate_webhook()
    {
        // 成功後に同じ Webhook がもう一度来ても PrOpened のまま（巻き戻らない）。
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "C5", 0),
            Ev(EventType.Triaged, "C5", 1, "Classified as SchemaDrift (fixable=True, conf 90)"),
            Ev(EventType.PrCreated, "C5", 2, "https://github.com/x/y/pull/9"),
            Ev(EventType.WebhookReceived, "C5", 3),
            Ev(EventType.DuplicateSuppressed, "C5", 4),
        };

        Assert.Equal(CaseState.PrOpened, Assert.Single(CaseProjection.Fold(events)).State);
    }

    [Fact]
    public void Review_pending_shows_Reviewing()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "R1", 0),
            Ev(EventType.Triaged, "R1", 1, "Classified as SchemaDrift (conf 90)"),
            Ev(EventType.FixAttemptStarted, "R1", 2),
            Ev(EventType.TestVerifyCompleted, "R1", 3),
            Ev(EventType.ReviewStarted, "R1", 4),
        };

        Assert.Equal(CaseState.Reviewing, Assert.Single(CaseProjection.Fold(events)).State);
    }

    [Fact]
    public void Review_approved_then_pr_ends_in_PrOpened()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "R2", 0),
            Ev(EventType.Triaged, "R2", 1, "Classified as SchemaDrift (conf 90)"),
            Ev(EventType.FixAttemptStarted, "R2", 2),
            Ev(EventType.TestVerifyCompleted, "R2", 3),
            Ev(EventType.ReviewStarted, "R2", 4),
            Ev(EventType.ReviewApproved, "R2", 5),
            Ev(EventType.PrCreated, "R2", 6, "https://github.com/x/y/pull/3"),
        };

        Assert.Equal(CaseState.PrOpened, Assert.Single(CaseProjection.Fold(events)).State);
    }

    [Fact]
    public void Review_rejected_loops_back_to_Fixing()
    {
        // 却下後に再び修正に戻ると、状態は Verified のままにならず Fixing になること。
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "R3", 0),
            Ev(EventType.Triaged, "R3", 1, "Classified as SchemaDrift (conf 90)"),
            Ev(EventType.FixAttemptStarted, "R3", 2),
            Ev(EventType.TestVerifyCompleted, "R3", 3),
            Ev(EventType.ReviewStarted, "R3", 4),
            Ev(EventType.ReviewRejected, "R3", 5),
            Ev(EventType.FixAttemptStarted, "R3", 6),
        };

        var c = Assert.Single(CaseProjection.Fold(events));
        Assert.Equal(CaseState.Fixing, c.State);
        Assert.Equal(2, c.FixAttempts);
    }

    [Fact]
    public void In_flight_case_is_not_regressed_by_a_duplicate_webhook()
    {
        // レビュー中に同じ Webhook がもう一度来ても Detected に巻き戻らないこと。
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "R4", 0),
            Ev(EventType.Triaged, "R4", 1, "Classified as SchemaDrift (conf 90)"),
            Ev(EventType.FixAttemptStarted, "R4", 2),
            Ev(EventType.TestVerifyCompleted, "R4", 3),
            Ev(EventType.ReviewStarted, "R4", 4),
            Ev(EventType.WebhookReceived, "R4", 5),
            Ev(EventType.DuplicateSuppressed, "R4", 6),
        };

        Assert.Equal(CaseState.Reviewing, Assert.Single(CaseProjection.Fold(events)).State);
    }

    [Fact]
    public void State_is_order_independent()
    {
        var ordered = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "C6", 0),
            Ev(EventType.Triaged, "C6", 1, "Classified as SchemaDrift (conf 90)"),
            Ev(EventType.FixAttemptStarted, "C6", 2),
            Ev(EventType.TestVerifyCompleted, "C6", 3),
        };
        var shuffled = new List<AgentEvent> { ordered[3], ordered[0], ordered[2], ordered[1] };

        Assert.Equal(
            CaseProjection.Fold(ordered).Single().State,
            CaseProjection.Fold(shuffled).Single().State);
    }

    [Fact]
    public void Groups_distinct_cases_and_ignores_eventless_caseid()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "A", 0),
            Ev(EventType.WebhookReceived, "B", 1),
            new(DateTimeOffset.UtcNow, EventType.WebhookReceived, "no case", null, CaseId: null),
        };

        var cases = CaseProjection.Fold(events);
        Assert.Equal(2, cases.Count);
        Assert.Contains(cases, c => c.CaseId == "A");
        Assert.Contains(cases, c => c.CaseId == "B");
    }

    [Fact]
    public void Cases_are_ordered_by_last_updated_desc()
    {
        var events = new List<AgentEvent>
        {
            Ev(EventType.WebhookReceived, "old", 0),
            Ev(EventType.WebhookReceived, "new", 10),
        };

        Assert.Equal("new", CaseProjection.Fold(events)[0].CaseId);
    }
}
