using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Versio.App.Views;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The clone dialog: does Clone stay shut until the remote has been verified?
/// </summary>
/// <remarks>
/// <para>
/// That rule (plan §9.2) is the whole reason this dialog is not two text boxes and a button.
/// A clone of a repository full of PLC binaries runs for minutes, and the two failures anybody
/// actually hits — a typo in the URL, and credentials that do not work here — both surface in
/// <c>ls-remote</c> in under a second.
/// </para>
/// <para>
/// Git is injected, so these run with no network, no repository and no process: what is under
/// test is the <i>conversation</i>, which is the part with the interesting mistakes in it.
/// </para>
/// </remarks>
public sealed class ClonePromptWindowTests
{
    private static GitResult Ok() => new() { Success = true, ExitCode = 0 };

    private static GitResult Fail(string reason) =>
        new() { Success = false, ExitCode = 128, StdErr = reason };

    private static T Control<T>(ClonePromptWindow window, string name) where T : FrameworkElement
    {
        var field = typeof(ClonePromptWindow).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(window));
    }

    /// <summary>A dialog wired to a git that always answers the same way.</summary>
    private static ClonePromptWindow Dialog(
        Func<string, GitResult>? verify = null,
        Func<string, string, GitResult>? clone = null,
        string? folder = @"C:\repos") =>
        new(
            (url, _) => Task.FromResult((verify ?? (_ => Ok()))(url)),
            (url, destination, _, _) =>
                Task.FromResult((clone ?? ((_, _) => Ok()))(url, destination)),
            folder);

    // Driven through the awaitable work rather than by raising Click. The handlers are
    // async void, so a raised click returns at the first await and finishes during whichever
    // test happens to pump the dispatcher next - which took the whole test host down before
    // the dialog was refactored to expose these.

    [Fact]
    public void The_dialog_builds_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            var window = Dialog();

            Assert.Equal("Clone a repository", window.Title);
            Assert.Null(window.ClonedPath);

            // Both explanation lines say something from the start. A dialog that opens with
            // two blank labels reads as broken rather than as waiting — the same defect a
            // test caught in the branch dialog.
            Assert.NotEqual(string.Empty, Control<TextBlock>(window, "DestinationMessage").Text);
        });
    }

    [Fact]
    public void Clone_is_dead_until_a_remote_has_been_verified()
    {
        StaHarness.Run(async () =>
        {
            var window = Dialog();
            var cloneButton = Control<Button>(window, "CloneButton");

            Control<TextBox>(window, "UrlBox").Text = "https://example.test/machine.git";

            // A URL and a folder are not enough. Only ls-remote says the thing exists and
            // that the credentials work.
            Assert.False(cloneButton.IsEnabled);

            await window.VerifyAsync();
            Assert.True(cloneButton.IsEnabled);
        });
    }

    [Fact]
    public void Editing_the_url_after_verifying_it_shuts_Clone_again()
    {
        StaHarness.Run(async () =>
        {
            var window = Dialog();
            var url = Control<TextBox>(window, "UrlBox");
            var cloneButton = Control<Button>(window, "CloneButton");

            url.Text = "https://example.test/machine.git";
            await window.VerifyAsync();
            Assert.True(cloneButton.IsEnabled);

            // The failure this dialog is shaped to prevent: verify one remote, paste another,
            // and clone the second on the strength of the first.
            url.Text = "https://example.test/something-else.git";
            Assert.False(cloneButton.IsEnabled);
        });
    }

    [Fact]
    public void A_remote_that_cannot_be_reached_is_reported_in_gits_own_words()
    {
        StaHarness.Run(async () =>
        {
            var window = Dialog(verify: _ => Fail("fatal: repository not found"));

            Control<TextBox>(window, "UrlBox").Text = "https://example.test/typo.git";
            await window.VerifyAsync();

            // Git names the actual problem far better than anything invented here would.
            Assert.Contains("repository not found",
                Control<TextBlock>(window, "VerifyMessage").Text, StringComparison.Ordinal);
            Assert.False(Control<Button>(window, "CloneButton").IsEnabled);
        });
    }

    [Fact]
    public void A_failed_clone_leaves_the_dialog_open_with_the_reason()
    {
        StaHarness.Run(async () =>
        {
            var window = Dialog(clone: (_, _) => Fail("fatal: destination path already exists"));

            Control<TextBox>(window, "UrlBox").Text = "https://example.test/machine.git";
            await window.VerifyAsync();
            await window.CloneAsync();

            // Nothing to open, and the dialog stays up so the folder can be changed rather
            // than the whole conversation repeated.
            Assert.Null(window.ClonedPath);
            Assert.Contains("already exists",
                Control<TextBlock>(window, "DestinationMessage").Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_successful_clone_reports_where_it_landed()
    {
        StaHarness.Run(async () =>
        {
            string? cloneTarget = null;
            var window = Dialog(clone: (_, destination) =>
            {
                cloneTarget = destination;
                return Ok();
            });

            Control<TextBox>(window, "UrlBox").Text = "https://example.test/machine.git";
            await window.VerifyAsync();
            await window.CloneAsync();

            // The path is what the shell opens afterwards. A clone that finishes and leaves
            // the user to go and find the folder has done the work and withheld the result.
            Assert.Equal(Path.Combine(@"C:\repos", "machine"), cloneTarget);
            Assert.Equal(cloneTarget, window.ClonedPath);
        });
    }

    [Theory]
    [InlineData("https://example.test/machine.git", "machine")]
    [InlineData("https://example.test/machine", "machine")]
    [InlineData("https://example.test/owner/Machine.Git", "Machine")]
    [InlineData("git@example.test:owner/machine.git", "machine")]
    [InlineData("ssh://git@example.test/owner/machine.git", "machine")]
    [InlineData(@"C:\repos\machine", "machine")]
    [InlineData("https://example.test/machine/", "machine")]
    public void The_folder_name_is_the_one_git_itself_would_choose(string url, string expected)
    {
        // Including the scp-like form, whose last separator is a colon rather than a slash —
        // the case a naive split on '/' gets wrong by producing "owner/machine".
        Assert.Equal(expected, ClonePromptWindow.RepositoryNameFrom(url));
    }

    [Fact]
    public void With_no_folder_chosen_there_is_no_destination_and_no_Clone()
    {
        StaHarness.Run(async () =>
        {
            var window = Dialog(folder: string.Empty);

            Control<TextBox>(window, "UrlBox").Text = "https://example.test/machine.git";
            await window.VerifyAsync();

            // Verified, and still not enough: there is nowhere to put it.
            Assert.Null(window.Destination());
            Assert.False(Control<Button>(window, "CloneButton").IsEnabled);
        });
    }
}
