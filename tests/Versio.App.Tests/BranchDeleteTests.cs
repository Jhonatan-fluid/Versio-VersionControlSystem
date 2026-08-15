using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The delete dialog, and the deletion behind it, against real branches.
/// </summary>
/// <remarks>
/// <para>
/// The dialog is constructed against the real stylesheet for the reason <c>MainWindowTests</c>
/// documents at length: a <c>StaticResource</c> is a key until something asks for it, and the
/// first thing that asks is the window opening in front of somebody. It is also the second
/// window to want the <c>ResourceKey</c> converter, which is why that moved out of
/// <c>MainWindow.Resources</c> and into the stylesheet.
/// </para>
/// <para>
/// The flow tests never open a window: the shell asks through
/// <see cref="ShellViewModel.PromptForBranchDeletion"/>, so a test answers it directly. Every
/// rule worth pinning — <c>-d</c> before <c>-D</c>, per-branch reporting, moving off the branch
/// being deleted — lives on the shell side of that seam.
/// </para>
/// </remarks>
public sealed class BranchDeleteTests
{
    private static BranchCandidate Candidate(
        string name, BranchExistence existence = BranchExistence.Both,
        bool merged = true, bool current = false, bool isDefault = false) => new()
    {
        Name = name,
        Existence = existence,
        IsMerged = merged,
        IsCurrent = current,
        IsDefault = isDefault,
    };

    // ------------------------------------------------------------------ the window

    [Fact]
    public void The_dialog_constructs_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchDeleteWindow([Candidate("feature/x")]);

            Assert.NotNull(window.Content);
        });
    }

    [Fact]
    public void It_lays_out_with_rows_in_it()
    {
        // A template's StaticResources resolve when a row is realised, not when the XAML is
        // parsed - the lesson GP-E learned by crashing the test host.
        StaHarness.Run(() =>
        {
            var window = new BranchDeleteWindow(
                [Candidate("feature/x"), Candidate("main", isDefault: true)]);

            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(540, 640));
            content.Arrange(new Rect(0, 0, 540, 640));
            content.UpdateLayout();

            Assert.True(content.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void Nothing_chosen_means_the_delete_button_is_dead_and_says_why()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchDeleteWindow([Candidate("feature/x")]);

            Assert.False(window.Current.CanProceed);
            Assert.Contains("at least one", window.Current.BlockedReason);
        });
    }

    [Fact]
    public void The_default_branch_cannot_be_ticked_however_it_is_asked()
    {
        // Belt and braces beside the disabled checkbox: the row collection is read by code as
        // well as bound to a control, and the default branch is the fallback the current-branch
        // case checks out.
        StaHarness.Run(() =>
        {
            var window = new BranchDeleteWindow([Candidate("main", isDefault: true)]);
            var row = new BranchDeleteRow(Candidate("main", isDefault: true));

            row.IsChosen = true;

            Assert.False(row.IsChosen);
            Assert.False(window.Current.CanProceed);
        });
    }

    [Fact]
    public void Unmerged_work_needs_an_acknowledgement_that_only_appears_when_it_applies()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchDeleteWindow(
                [Candidate("tidy"), Candidate("feature/x", merged: false)]);

            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var force = Assert.IsType<CheckBox>(window.FindName("ForceCheck"));

            // A permanent "delete unmerged work" checkbox would be read once and never again.
            Assert.Equal(Visibility.Collapsed, force.Visibility);

            var rows = Assert.IsAssignableFrom<IEnumerable<BranchDeleteRow>>(
                ((ListBox)window.FindName("BranchList")!).ItemsSource);

            rows.First(r => r.Name == "feature/x").IsChosen = true;
            content.UpdateLayout();

            Assert.Equal(Visibility.Visible, force.Visibility);
            Assert.False(window.Current.CanProceed);
            Assert.Contains("not fully merged", window.Current.BlockedReason);

            force.IsChecked = true;
            Assert.True(window.Current.CanProceed);
        });
    }

    [Fact]
    public void The_summary_says_what_is_about_to_happen()
    {
        StaHarness.Run(() =>
        {
            var window = new BranchDeleteWindow(
                [Candidate("a"), Candidate("b", BranchExistence.LocalOnly)]);

            var rows = Assert.IsAssignableFrom<IEnumerable<BranchDeleteRow>>(
                ((ListBox)window.FindName("BranchList")!).ItemsSource).ToList();

            foreach (var row in rows)
            {
                row.IsChosen = true;
            }

            var summary = Assert.IsType<TextBlock>(window.FindName("SummaryText"));

            Assert.Contains("2 branches selected", summary.Text);
            Assert.Contains("local only", summary.Text);
        });
    }

    // ------------------------------------------------------------------ the flow

    private static ShellViewModel Shell(Func<IReadOnlyList<BranchCandidate>, DeletePlan?> answer)
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        var shell = new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);

        var prompts = shell.Asked();
        prompts.BranchDeletion = answer;
        prompts.Destructive = _ => true;

        return shell;
    }

    private static DeletePlan Plan(
        IReadOnlyList<BranchCandidate> candidates, string name,
        DeleteScope scope = DeleteScope.Local, bool force = false) => new()
    {
        Selected = [.. candidates.Where(c => c.Name == name)],
        Scope = scope,
        ForceAcknowledged = force,
    };

    [Fact]
    public async Task A_remote_only_branch_is_listed_at_all()
    {
        // It was not, before this: the branch list read refs/heads. A colleague's branch was
        // invisible and the dropdown looked like the whole repository.
        using var repo = new TempRepo(published: true);
        repo.Git("push -q origin main:theirs");
        repo.Git("fetch -q");

        using var shell = Shell(_ => null);
        await shell.OpenAsync(repo.Path);

        Assert.Contains(shell.Branches, b => b.Name == "theirs" && b.IsRemoteOnly);
        Assert.Contains(shell.Branches, b => b.Name == "theirs" && b.Where == "remote");
    }

    [Fact]
    public async Task The_default_branch_is_resolved_from_convention_when_nothing_else_says()
    {
        using var repo = new TempRepo();
        using var shell = Shell(_ => null);

        await shell.OpenAsync(repo.Path);

        Assert.True(shell.DefaultBranch.IsKnown);
        Assert.Equal("main", shell.DefaultBranch.Name);
    }

    [Fact]
    public async Task Deleting_a_merged_branch_removes_it()
    {
        using var repo = new TempRepo();
        repo.Git("branch tidy");

        using var shell = Shell(c => Plan(c, "tidy"));
        await shell.OpenAsync(repo.Path);

        Assert.Contains(shell.Branches, b => b.Name == "tidy");

        await Eventually.Press(shell.DeleteBranchesCommand, "delete branches");
        await Eventually.Until(() => shell.Branches.All(b => b.Name != "tidy"),
                               "the branch went");
    }

    [Fact]
    public async Task A_plan_that_cannot_proceed_deletes_nothing()
    {
        // The dialog would never hand this back - its button is disabled - but the shell is the
        // last gate and must not rely on a window having been honest. An unmerged branch with
        // no acknowledgement is exactly that plan.
        using var repo = new TempRepo();
        repo.Git("checkout -q -b feature/x");
        repo.Touch("seed.txt");
        repo.Git("commit -qam work");
        repo.Git("checkout -q main");

        using var shell = Shell(c => Plan(c, "feature/x"));
        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.DeleteBranchesCommand, "delete branches");
        await Task.Delay(500);

        Assert.Contains(shell.Branches, b => b.Name == "feature/x");
        Assert.Empty(shell.Notifications.Visible);
    }

    [Fact]
    public async Task Acknowledging_forces_it_through()
    {
        using var repo = new TempRepo();
        repo.Git("checkout -q -b feature/x");
        repo.Touch("seed.txt");
        repo.Git("commit -qam work");
        repo.Git("checkout -q main");

        using var shell = Shell(c => Plan(c, "feature/x", force: true));
        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.DeleteBranchesCommand, "delete branches");
        await Eventually.Until(() => shell.Branches.All(b => b.Name != "feature/x"),
                               "the branch went");
    }

    [Fact]
    public async Task Deleting_the_branch_you_are_on_moves_you_first()
    {
        // git refuses outright, so this is handled explicitly rather than surfacing the
        // refusal. The switch goes through the ordinary branch-switch path, which means a dirty
        // tree meets the auto-stash exactly as it does anywhere else.
        using var repo = new TempRepo();
        repo.Git("checkout -q -b feature/x");

        using var shell = Shell(c => Plan(c, "feature/x"));
        await shell.OpenAsync(repo.Path);

        Assert.Equal("feature/x", shell.BranchName);

        await Eventually.Press(shell.DeleteBranchesCommand, "delete branches");
        await Eventually.Until(() => shell.BranchName == "main", "the switch happened");
        await Eventually.Until(() => shell.Branches.All(b => b.Name != "feature/x"),
                               "the branch went");
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_is_reported_per_branch()
    {
        // A real partial failure, produced rather than contrived: the local delete succeeds and
        // the push that would remove the remote copy cannot reach anything. "Some branches
        // could not be deleted" would send somebody to the terminal to find out which.
        using var repo = new TempRepo(published: true);
        repo.Git("branch tidy");
        repo.Git("push -q origin tidy");
        repo.Git("fetch -q");
        repo.Git("remote set-url origin C:/versio/definitely/not/a/repository");

        using var shell = Shell(c => Plan(c, "tidy", DeleteScope.LocalAndRemote));
        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.DeleteBranchesCommand, "delete branches");
        await Eventually.Until(() => shell.Notifications.Visible.Count > 0, "something was said");

        var card = shell.Notifications.Visible[0];

        // Named, with git's own words underneath.
        Assert.Contains("tidy", card.Detail);
        Assert.NotEqual(string.Empty, card.Detail);
    }

    [Fact]
    public async Task Git_gets_the_last_word_when_our_merged_check_is_wrong()
    {
        // WHY -d IS TRIED BEFORE -D, DEMONSTRATED RATHER THAN ASSERTED.
        //
        // `git branch --merged main` and `git branch -d` do not ask the same question. --merged
        // compares against one branch; -d also refuses a branch that is ahead of its own
        // UPSTREAM. So a branch can be reported merged by the check the dialog uses and still be
        // carrying work nobody else has:
        //
        //     warning: not deleting branch 'tidy' that is not yet merged to
        //              'refs/remotes/origin/tidy', even though it is merged to HEAD
        //
        // The plan therefore says it may proceed and needs no acknowledgement. Attempting -d
        // first is what turns that into a refusal the user sees rather than a silent -D.
        using var repo = new TempRepo(published: true);

        repo.Git("checkout -q -b tidy");
        repo.Git("push -q -u origin tidy");
        repo.Touch("seed.txt");
        repo.Git("commit -qam work");
        repo.Git("checkout -q main");
        repo.Git("merge -q tidy");

        using var shell = Shell(c => Plan(c, "tidy"));
        await shell.OpenAsync(repo.Path);

        // Reported merged, so no acknowledgement was asked for.
        await Eventually.Press(shell.DeleteBranchesCommand, "delete branches");
        await Eventually.Until(() => shell.Notifications.Visible.Count > 0, "something was said");

        // And the branch is still here, because git said no and nothing escalated past it.
        Assert.Contains(shell.Branches, b => b.Name == "tidy");

        // Nothing succeeded, so the headline says so plainly rather than counting to zero.
        Assert.Equal("No branches were deleted.", shell.Notifications.Visible[0].Headline);

        // And git's own words are underneath, naming the branch and the upstream it is ahead
        // of - which is the sentence that explains what to do next.
        Assert.Contains("tidy", shell.Notifications.Visible[0].Detail);
        Assert.Contains("not fully merged", shell.Notifications.Visible[0].Detail);
    }

    [Fact]
    public async Task Cancelling_deletes_nothing()
    {
        using var repo = new TempRepo();
        repo.Git("branch tidy");

        using var shell = Shell(_ => null);
        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.DeleteBranchesCommand, "delete branches");
        await Task.Delay(500);

        Assert.Contains(shell.Branches, b => b.Name == "tidy");
    }

    [Fact]
    public async Task Nothing_is_offered_when_there_is_only_one_branch()
    {
        using var repo = new TempRepo();
        using var shell = Shell(_ => null);

        await shell.OpenAsync(repo.Path);

        Assert.Single(shell.Branches);
        Assert.False(shell.DeleteBranchesCommand.CanExecute(null));
    }
}
