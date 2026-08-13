using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Versio.App.Views;
using Versio.Core.Guarding;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The guard dialog: does it build, and does it offer what the verdict said?
/// </summary>
/// <remarks>
/// The first test here earns the file, for the reason the branch prompt's does: a dialog is
/// parsed the moment it is first opened, so a missing style key is not a compile error but an
/// exception in front of a user weeks later. This one is worse than most if it fails — it is
/// the dialog that stands between somebody and losing work, so the moment it is needed is
/// exactly the moment it must not throw.
/// </remarks>
public sealed class GuardPromptWindowTests
{
    private static StackPanel ButtonRow(GuardPromptWindow window)
    {
        var field = typeof(GuardPromptWindow).GetField(
            "Buttons", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(field);
        return Assert.IsType<StackPanel>(field!.GetValue(window));
    }

    private static IReadOnlyList<Button> Buttons(GuardPromptWindow window) =>
        [.. ButtonRow(window).Children.OfType<Button>()];

    private static GuardPrompt PromptFor(GuardVerdict verdict) =>
        GuardNarrative.Prompt(verdict)
        ?? throw new InvalidOperationException("the verdict produced no prompt");

    private static GuardVerdict DirtySwitch() => InterruptionGuard.Evaluate(
        GuardIntent.SwitchBranch,
        WorkSnapshot.Idle,
        new WorkingTreeSnapshot { DirtyPaths = ["a.st", "b.st"] },
        target: "feature/pumps");

    private static GuardVerdict RunningImport() => InterruptionGuard.Evaluate(
        GuardIntent.CloseApp,
        WorkSnapshot.Of(new ActiveWork(WorkKind.Import, "Importing into Machine…",
                                       TimeSpan.FromSeconds(12))),
        WorkingTreeSnapshot.Clean);

    [Fact]
    public void The_dialog_builds_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            var window = new GuardPromptWindow(PromptFor(DirtySwitch()));

            Assert.NotNull(window);
            Assert.NotEmpty(Buttons(window));
        });
    }

    [Fact]
    public void Every_choice_the_verdict_offered_becomes_a_button()
    {
        StaHarness.Run(() =>
        {
            var verdict = DirtySwitch();
            var window = new GuardPromptWindow(PromptFor(verdict));

            Assert.Equal(verdict.Choices.Count, Buttons(window).Count);

            foreach (var choice in verdict.Choices)
            {
                Assert.Contains(Buttons(window),
                    b => (string)b.Content == GuardNarrative.Label(choice));
            }
        });
    }

    [Fact]
    public void The_recommended_choice_is_the_default_and_sits_on_the_right()
    {
        // The guard orders best-first; a button row reads left to right with the default on
        // the right, so the dialog reverses it. Getting this backwards would put "discard my
        // changes" under the Enter key.
        StaHarness.Run(() =>
        {
            var verdict = DirtySwitch();
            var buttons = Buttons(new GuardPromptWindow(PromptFor(verdict)));

            var expected = GuardNarrative.Label(verdict.Choices[0]);

            Assert.Equal(expected, (string)buttons[^1].Content);
            Assert.True(buttons[^1].IsDefault);
            Assert.All(buttons.Take(buttons.Count - 1), b => Assert.False(b.IsDefault));
        });
    }

    [Fact]
    public void Escape_maps_to_cancel_wherever_it_sits()
    {
        StaHarness.Run(() =>
        {
            var buttons = Buttons(new GuardPromptWindow(PromptFor(DirtySwitch())));
            var cancel = Assert.Single(buttons, b => b.IsCancel);

            Assert.Equal(GuardNarrative.Label(GuardChoice.Cancel), (string)cancel.Content);
        });
    }

    [Fact]
    public void A_refusal_offers_one_acknowledgement_and_no_choice()
    {
        // Nothing would make closing safe while an import is writing, so the dialog states
        // rather than asks. A Cancel beside an OK would imply an alternative that is not there.
        StaHarness.Run(() =>
        {
            var window = new GuardPromptWindow(PromptFor(RunningImport()));
            var button = Assert.Single(Buttons(window));

            Assert.Equal("OK", (string)button.Content);
            Assert.True(button.IsDefault);
            Assert.True(button.IsCancel);
        });
    }

    [Fact]
    public void Acknowledging_a_refusal_is_not_reported_as_a_choice()
    {
        // A caller that read the OK as a decision would act on it. There was nothing to decide.
        StaHarness.Run(() =>
        {
            var window = new GuardPromptWindow(PromptFor(RunningImport()));

            Assert.Null(window.ChoiceResult);
        });
    }

    [Fact]
    public void The_body_is_the_narrative_verbatim()
    {
        // The dialog renders; it does not phrase. A test that let this file compose its own
        // sentence would be the moment the wording stopped living in one place.
        StaHarness.Run(() =>
        {
            var verdict = DirtySwitch();
            var window = new GuardPromptWindow(PromptFor(verdict));

            var body = typeof(GuardPromptWindow)
                .GetField("Body", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window) as TextBlock;

            Assert.NotNull(body);
            Assert.Equal(GuardNarrative.Body(verdict), body!.Text);
        });
    }

    [Fact]
    public void Clicking_a_button_returns_that_choice()
    {
        StaHarness.Run(() =>
        {
            var verdict = DirtySwitch();

            // Every button, not one of them. The row is the reverse of the verdict's order, so
            // an off-by-one here is exactly the bug that would return "discard" for a click on
            // "stash" — and a test that pressed a single button could pass with the mapping
            // inverted. A window per click, because a click closes the one it lands on.
            for (var i = 0; i < verdict.Choices.Count; i++)
            {
                var window = new GuardPromptWindow(PromptFor(verdict));
                var target = Buttons(window)[i];
                var expected = verdict.Choices[verdict.Choices.Count - 1 - i];

                Assert.Equal(GuardNarrative.Label(expected), (string)target.Content);

                // ShowDialog is never called, so setting DialogResult would throw. The handler
                // is invoked directly, which is what a click does to it.
                var click = target.GetType()
                    .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(click);

                try
                {
                    click!.Invoke(target, null);
                }
                catch (TargetInvocationException ex)
                    when (ex.InnerException is InvalidOperationException)
                {
                    // DialogResult on a window that was never shown modally. The choice is
                    // recorded before that line, which is the thing under test.
                }

                Assert.Equal(expected, window.ChoiceResult);
            }
        });
    }
}
