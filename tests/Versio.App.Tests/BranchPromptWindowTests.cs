using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The new-branch dialog: does it build, and does it refuse what git would?
/// </summary>
/// <remarks>
/// <para>
/// The first test here is the one that earns the project. A dialog is <b>parsed at the moment
/// it is first opened</b>, so a style renamed in the stylesheet, or a brush key that never
/// existed, is not a compile error and not a binding warning — it is an exception the first
/// time a user clicks the button, weeks later. Constructing it in a test moves that to the
/// build.
/// </para>
/// <para>
/// It also closes the one hole left by <c>--trace-bindings</c>, which the shell is otherwise
/// verified with: the tracer can only report on windows that were opened, and a modal nobody
/// clicked is a window that was never parsed.
/// </para>
/// </remarks>
public sealed class BranchPromptWindowTests
{
    /// <summary>Read a private control off the dialog, so the test drives what the user does.</summary>
    /// <remarks>
    /// Reflection rather than making the fields public: the alternative is widening the
    /// dialog's surface for the benefit of a test, and these names are checked by the compiler
    /// on the other side — a renamed control fails here loudly.
    /// </remarks>
    private static T Control<T>(BranchPromptWindow window, string name) where T : FrameworkElement
    {
        var field = typeof(BranchPromptWindow).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(window));
    }

    [Fact]
    public void The_dialog_builds_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            // Every StaticResource in the XAML resolves here or this throws: FlatTextBox,
            // GhostButton, AccentButton, SectionLabel, and the brushes the code-behind looks
            // up by name.
            var window = new BranchPromptWindow();

            Assert.Equal("New branch", window.Title);
            Assert.Null(window.BranchNameResult);
        });
    }

    [Fact]
    public void The_naming_example_lives_in_the_message_line_not_in_a_watermark()
    {
        // The box is focused the moment the dialog opens, and a watermark is the one hint
        // focus hides — under the old template it sat beneath a blinking caret instead,
        // which read as text to delete (2026-10-14). The example has to be legible while
        // the person types, so it lives in the message line, where a caret cannot sit.
        StaHarness.Run(() =>
        {
            var window = new BranchPromptWindow();

            Assert.Null(Control<TextBox>(window, "NameBox").Tag);
            Assert.Contains("feature/what-this-changes",
                Control<TextBlock>(window, "Message").Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Create_is_disabled_until_something_valid_is_typed()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchPromptWindow();
            var button = Control<Button>(window, "CreateButton");
            var box = Control<TextBox>(window, "NameBox");

            // Empty: disabled, and the message explains what will happen rather than
            // scolding somebody for not having typed yet.
            Assert.False(button.IsEnabled);
            Assert.Contains("switched to", Control<TextBlock>(window, "Message").Text,
                            StringComparison.Ordinal);

            box.Text = "feature/library-export";
            Assert.True(button.IsEnabled);
        });
    }

    [Theory]
    [InlineData("feature branch", "spaces")]
    [InlineData("-dashed", "'-'")]
    [InlineData("a..b", "..")]
    [InlineData("ends.", "'.'")]
    [InlineData("x.lock", ".lock")]
    public void A_name_git_would_refuse_disables_Create_and_says_why(string name, string fragment)
    {
        StaHarness.Run(() =>
        {
            var window = new BranchPromptWindow();
            var box = Control<TextBox>(window, "NameBox");
            var button = Control<Button>(window, "CreateButton");
            var message = Control<TextBlock>(window, "Message");

            box.Text = name;

            Assert.False(button.IsEnabled);

            // The reason, not just a refusal. A greyed-out button with no sentence beside it
            // leaves the user with nothing to change — worse than letting git refuse, because
            // git at least says something.
            Assert.Contains(fragment, message.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_name_becoming_invalid_again_disables_Create()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchPromptWindow();
            var box = Control<TextBox>(window, "NameBox");
            var button = Control<Button>(window, "CreateButton");

            box.Text = "good-name";
            Assert.True(button.IsEnabled);

            // Typing on: the validation has to run in both directions. A check that only ever
            // enables would leave Create live over a name that has since gone bad.
            box.Text = "good-name ";
            Assert.False(button.IsEnabled);
        });
    }

    [Fact]
    public void The_message_changes_colour_between_a_refusal_and_an_explanation()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchPromptWindow();
            var box = Control<TextBox>(window, "NameBox");
            var message = Control<TextBlock>(window, "Message");

            box.Text = "feature/ok";
            var calm = message.Foreground;

            box.Text = "feature ok";
            var alarmed = message.Foreground;

            // Both come from the stylesheet, and they must not be the same brush — a refusal
            // rendered in the explanatory colour reads as advice.
            Assert.NotSame(calm, alarmed);
        });
    }

    [Fact]
    public void Enter_on_an_invalid_name_does_nothing()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchPromptWindow();
            var box = Control<TextBox>(window, "NameBox");

            box.Text = "no spaces allowed";

            // The Create button is IsDefault, so Enter reaches its handler whatever the
            // button's own enabled state is doing. Raising the click directly is what the
            // keyboard does, and the handler has to refuse on its own.
            Control<Button>(window, "CreateButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Null(window.BranchNameResult);
        });
    }
}
