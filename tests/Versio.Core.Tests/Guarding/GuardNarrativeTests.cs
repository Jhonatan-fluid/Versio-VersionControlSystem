using Versio.Core.Guarding;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Guarding;

/// <summary>
/// The wording of every refusal and every question.
/// </summary>
/// <remarks>
/// Tested like <see cref="SyncNarrative"/>, and for the same reason: a guard is by construction
/// the thing standing between somebody and what they were trying to do, so a refusal they
/// cannot understand is indistinguishable from a broken tool. These assertions are about
/// coverage, actionability and shape — not exact phrasing.
/// </remarks>
public sealed class GuardNarrativeTests
{
    public static TheoryData<GuardChoice> AllChoices => [.. Enum.GetValues<GuardChoice>()];

    public static TheoryData<GuardIntent> AllIntents => [.. Enum.GetValues<GuardIntent>()];

    public static TheoryData<GuardReason> AllReasons => [.. Enum.GetValues<GuardReason>()];

    private static GuardVerdict Verdict(
        GuardDecision decision,
        GuardReason reason,
        GuardIntent intent = GuardIntent.SwitchBranch,
        IReadOnlyList<ActiveWork>? work = null,
        IReadOnlyList<HeldLock>? locks = null,
        int dirty = 0,
        int conflicted = 0,
        string target = "main") => new()
        {
            Decision = decision,
            Intent = intent,
            Reason = reason,
            Work = work ?? [],
            Locks = locks ?? [],
            DirtyFileCount = dirty,
            ConflictedFileCount = conflicted,
            Target = target,
        };

    // ------------------------------------------------------------------------- coverage

    [Theory]
    [MemberData(nameof(AllChoices))]
    public void Every_choice_has_a_button_label(GuardChoice choice)
    {
        Assert.False(string.IsNullOrWhiteSpace(GuardNarrative.Label(choice)));
    }

    [Theory]
    [MemberData(nameof(AllIntents))]
    public void Every_intent_has_a_verb_phrase(GuardIntent intent)
    {
        var action = GuardNarrative.Action(intent);

        Assert.False(string.IsNullOrWhiteSpace(action));
        Assert.NotEqual("do that", action);
    }

    [Theory]
    [MemberData(nameof(AllReasons))]
    public void Every_reason_has_a_title_and_a_body(GuardReason reason)
    {
        var verdict = Verdict(GuardDecision.Ask, reason, dirty: 2, conflicted: 2,
                              work: [new ActiveWork(WorkKind.Export, "Exporting…", TimeSpan.FromSeconds(5))],
                              locks: [new HeldLock("b-1", "Machine", SyncDirection.TextAuthoritative)]);

        Assert.False(string.IsNullOrWhiteSpace(GuardNarrative.Title(verdict)));
        Assert.False(string.IsNullOrWhiteSpace(GuardNarrative.Body(verdict)));
    }

    [Theory]
    [MemberData(nameof(AllReasons))]
    public void Every_reason_has_a_single_line_for_the_log(GuardReason reason)
    {
        // This is read in a list of a hundred other lines. A paragraph there is worse than a
        // sentence, and a newline would break the row.
        foreach (var decision in Enum.GetValues<GuardDecision>())
        {
            var line = GuardNarrative.Describe(Verdict(decision, reason, dirty: 3, conflicted: 1,
                locks: [new HeldLock("b-1", "Machine", SyncDirection.ProjectAuthoritative)],
                work: [new ActiveWork(WorkKind.Import, "Importing…", TimeSpan.FromSeconds(9))]));

            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.DoesNotContain('\n', line);
        }
    }

    // ------------------------------------------------------------------------ the prompt

    [Fact]
    public void Proceeding_produces_no_prompt_at_all()
    {
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.CloseApp, WorkSnapshot.Idle, WorkingTreeSnapshot.Clean);

        Assert.Null(GuardNarrative.Prompt(verdict));
    }

    [Fact]
    public void A_question_carries_its_choices_into_the_prompt()
    {
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.SwitchBranch,
            WorkSnapshot.Idle,
            new WorkingTreeSnapshot { DirtyPaths = ["a.st", "b.st"] },
            target: "feature/pumps");

        var prompt = Assert.IsType<GuardPrompt>(GuardNarrative.Prompt(verdict));

        Assert.Equal(verdict.Choices, prompt.Choices);
        Assert.Equal(GuardChoice.StashAndProceed, prompt.Recommended);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Title));
    }

    // ---------------------------------------------------------------- naming the remedy

    [Fact]
    public void Refusing_for_a_lock_says_what_to_do_about_it()
    {
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.SwitchBranch,
            WorkSnapshot.Idle,
            new WorkingTreeSnapshot { HeldLocks = [new HeldLock("b-1", "Machine", SyncDirection.TextAuthoritative)] },
            target: "main");

        var body = GuardNarrative.Body(verdict);

        Assert.Contains("Machine", body, StringComparison.Ordinal);
        Assert.Contains("Push or discard", body, StringComparison.Ordinal);
        Assert.Contains("on main", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusing_for_a_run_says_when_it_can_be_tried_again()
    {
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.CloseApp,
            WorkSnapshot.Of(new ActiveWork(WorkKind.Import, "Importing into Machine…", TimeSpan.FromSeconds(12))),
            WorkingTreeSnapshot.Clean);

        var body = GuardNarrative.Body(verdict);

        Assert.Contains("Importing into Machine…", body, StringComparison.Ordinal);
        Assert.Contains("12 seconds", body, StringComparison.Ordinal);
        Assert.Contains(GuardNarrative.Action(GuardIntent.CloseApp), body, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusing_for_conflicts_sends_the_user_to_the_merge_and_not_to_the_lock()
    {
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.Pull, WorkSnapshot.Idle, new WorkingTreeSnapshot { ConflictedPaths = ["a.st"] });

        var body = GuardNarrative.Body(verdict);

        Assert.Contains("Resolve them", body, StringComparison.Ordinal);
        Assert.DoesNotContain("direction lock", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Offering_to_stash_says_the_work_comes_back()
    {
        // The whole reason stash leads: it is the only option that keeps the changes, and the
        // user has to be told that before they can pick it over "discard".
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.SwitchBranch, WorkSnapshot.Idle, new WorkingTreeSnapshot { DirtyPaths = ["a.st"] });

        var body = GuardNarrative.Body(verdict);

        Assert.Contains("offers them back", body, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- the small print

    [Theory]
    [InlineData(0.4, "less than a second")]
    [InlineData(1, "1 second")]
    [InlineData(21, "21 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(90, "1 m 30 s")]
    public void Elapsed_time_reads_as_an_answer_rather_than_a_measurement(
        double seconds, string expected)
    {
        var verdict = Verdict(GuardDecision.Ask, GuardReason.CancellableWork,
            work: [new ActiveWork(WorkKind.Export, "Exporting…", TimeSpan.FromSeconds(seconds))]);

        Assert.Contains(expected, GuardNarrative.Body(verdict), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 file has")]
    [InlineData(3, "3 files have")]
    public void File_counts_agree_with_their_verb(int count, string expected)
    {
        var verdict = Verdict(GuardDecision.Ask, GuardReason.DirtyWorkingTree, dirty: count);

        Assert.Contains(expected, GuardNarrative.Body(verdict), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_operations_are_listed_rather_than_summed_into_one_sentence()
    {
        var verdict = Verdict(GuardDecision.Refuse, GuardReason.UninterruptibleWork, work:
        [
            new ActiveWork(WorkKind.Import, "Importing into A…", TimeSpan.FromSeconds(9)),
            new ActiveWork(WorkKind.GitPush, "Pushing…", TimeSpan.FromSeconds(3)),
        ]);

        var body = GuardNarrative.Body(verdict);

        Assert.Contains("Importing into A…", body, StringComparison.Ordinal);
        Assert.Contains("Pushing…", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Work_with_no_caption_still_names_itself()
    {
        // The description normally comes from the progress bar's caption. A run started
        // without one must not produce a sentence with a hole in it.
        var verdict = Verdict(GuardDecision.Refuse, GuardReason.UninterruptibleWork,
            work: [new ActiveWork(WorkKind.Import, "", TimeSpan.FromSeconds(4))]);

        var body = GuardNarrative.Body(verdict);

        Assert.Contains("Importing", body, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lock_is_described_in_the_words_the_project_panel_already_uses()
    {
        // Shared with SyncNarrative on purpose: the dialog and the row beside it must not
        // invent two vocabularies for one state.
        var verdict = Verdict(GuardDecision.Refuse, GuardReason.HeldDirectionLock,
            locks: [new HeldLock("b-1", "Machine", SyncDirection.TextAuthoritative)]);

        Assert.Contains(SyncNarrative.Label(SyncDirection.TextAuthoritative),
                        GuardNarrative.Body(verdict), StringComparison.Ordinal);
    }
}
