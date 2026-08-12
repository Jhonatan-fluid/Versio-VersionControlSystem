using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// The wording of the direction lock.
/// </summary>
/// <remarks>
/// Tested like the state machine it describes, because it is part of the same feature: a
/// refusal the user cannot understand is indistinguishable from a broken tool, and that is
/// the most likely way this one gets abandoned. These assertions are about coverage and
/// actionability, not exact phrasing.
/// </remarks>
public sealed class SyncNarrativeTests
{
    public static TheoryData<SyncDirection> AllDirections =>
        [.. Enum.GetValues<SyncDirection>()];

    public static TheoryData<SyncVerdict> AllVerdicts =>
        [.. Enum.GetValues<SyncVerdict>()];

    [Theory]
    [MemberData(nameof(AllDirections))]
    public void Every_direction_has_a_sentence_and_a_label(SyncDirection direction)
    {
        var label = SyncNarrative.Label(direction);
        var sentence = SyncNarrative.Describe(direction);

        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.NotEqual("unknown", label);
        Assert.EndsWith(".", sentence, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllVerdicts))]
    public void Every_verdict_has_a_sentence(SyncVerdict verdict)
    {
        var outcome = new SyncOutcome
        {
            Verdict = verdict,
            BindingId = "b-1",
            Explanation = "because",
            Duration = TimeSpan.FromSeconds(21.4),
        };

        Assert.False(string.IsNullOrWhiteSpace(SyncNarrative.Describe(outcome)));
    }

    [Theory]
    [InlineData(SyncDirection.ProjectAuthoritative)]
    [InlineData(SyncDirection.TextAuthoritative)]
    [InlineData(SyncDirection.Conflict)]
    [InlineData(SyncDirection.Failed)]
    public void A_state_that_blocks_someone_says_how_to_clear_it(SyncDirection direction)
    {
        // "Blocked" without a remedy is the message that generates the support call.
        var sentence = SyncNarrative.Describe(direction);

        Assert.True(
            sentence.Contains("push", StringComparison.OrdinalIgnoreCase) ||
            sentence.Contains("discard", StringComparison.OrdinalIgnoreCase) ||
            sentence.Contains("acknowledge", StringComparison.OrdinalIgnoreCase) ||
            sentence.Contains("pick a side", StringComparison.OrdinalIgnoreCase),
            $"'{sentence}' does not tell the user what to do next");
    }

    [Fact]
    public void A_completed_export_reports_its_cost_and_what_it_wrote()
    {
        var outcome = new SyncOutcome
        {
            Verdict = SyncVerdict.Completed,
            BindingId = "b-1",
            Explanation = "export completed",
            ChangedFiles = ["a.st", "b.st"],
            Duration = TimeSpan.FromSeconds(21.4),
        };

        var sentence = SyncNarrative.Describe(outcome);

        Assert.Contains("21.4 s", sentence);
        Assert.Contains("2 files", sentence);
    }

    [Fact]
    public void An_export_that_wrote_nothing_says_so_rather_than_claiming_success_vaguely()
    {
        var outcome = new SyncOutcome
        {
            Verdict = SyncVerdict.Completed,
            BindingId = "b-1",
            Explanation = "export completed",
            Duration = TimeSpan.FromSeconds(18),
        };

        Assert.Contains("no files changed", SyncNarrative.Describe(outcome));
    }

    [Fact]
    public void A_lock_refusal_carries_the_state_machines_own_reason()
    {
        var transition = SyncStateMachine.Next(
            SyncDirection.ProjectAuthoritative, SyncTrigger.TextChanged);

        var outcome = SyncOutcome.Refused(
            SyncVerdict.BlockedByLock, "b-1", transition.Reason, transition.State);

        // The specifics come from the state machine; the narrative only frames them, so the
        // two can never drift into contradicting each other.
        Assert.Contains(transition.Reason, SyncNarrative.Describe(outcome));
    }

    [Fact]
    public void A_failure_repeats_what_actually_failed()
    {
        var outcome = new SyncOutcome
        {
            Verdict = SyncVerdict.Failed,
            BindingId = "b-1",
            Explanation = "CODESYS exited with code 1",
        };

        Assert.Contains("CODESYS exited with code 1", SyncNarrative.Describe(outcome));
    }
}
