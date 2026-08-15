using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Settings;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Changing repository or branch while Versio is doing something.
/// </summary>
/// <remarks>
/// <para>
/// The dropdowns were the one way round every guard in the application: <c>Ready</c> gates every
/// git button on <c>Busy.IsBusy</c>, and neither <c>ComboBox</c> consulted anything at all. A
/// selection went straight to a checkout or a repository open, underneath whatever was running.
/// </para>
/// <para>
/// No git here. What is asserted is the decision and the state of the dropdown afterwards —
/// whether the switch was allowed, and whether the control still shows the truth when it was
/// not. Whether <c>git checkout</c> then works is git's business and is covered by the
/// integration suite.
/// </para>
/// </remarks>
public sealed class ContextSwitchGuardTests
{
    private sealed class Fixture
    {
        public required ShellViewModel Shell { get; init; }

        public List<GuardPrompt> Asked { get; } = [];

        public static Fixture Build(GuardChoice? answer)
        {
            var sink = new InMemoryLogSink();
            var logger = new VersioLogger(new SystemClock()).AddSink(sink);
            var shell = new ShellViewModel(logger, new LogViewModel(sink));
            var fixture = new Fixture { Shell = shell };

            shell.Asked().Guard = prompt =>
            {
                fixture.Asked.Add(prompt);
                return answer;
            };

            // Two branches, so there is somewhere to switch to and somewhere to snap back to.
            shell.Branches.Add(new BranchEntry(new BranchInfo
            {
                Name = "main",
                IsCurrent = true,
            }));

            shell.Branches.Add(new BranchEntry(new BranchInfo
            {
                Name = "feature/pumps",
                IsCurrent = false,
            }));

            return fixture;
        }

        public BranchEntry Current => Shell.Branches.Single(b => b.IsCurrent);

        public BranchEntry Other => Shell.Branches.Single(b => !b.IsCurrent);
    }

    // ------------------------------------------------------------- what the dropdowns allow

    [Fact]
    public void The_dropdowns_are_open_when_nothing_is_running()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        Assert.True(fixture.Shell.CanChangeContext);
    }

    [Fact]
    public void An_export_leaves_the_dropdowns_open()
    {
        // 17-31 seconds with a real choice to offer. Shutting the control would replace a
        // question that has a good answer with no question at all.
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting…");

        Assert.True(fixture.Shell.CanChangeContext);
    }

    [Fact]
    public void An_import_shuts_the_dropdowns()
    {
        // No safe answer exists, so the question is made unreachable rather than asked and
        // refused. A control that accepts a click, opens a dialog and snaps back is worse.
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var importing = fixture.Shell.Busy.Begin(WorkKind.Import, "Importing…");

        Assert.False(fixture.Shell.CanChangeContext);
    }

    [Fact]
    public void The_dropdowns_reopen_when_the_import_ends()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);
        var seen = new List<string>();

        fixture.Shell.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? string.Empty);

        fixture.Shell.Busy.Begin(WorkKind.Import, "Importing…").Dispose();

        Assert.True(fixture.Shell.CanChangeContext);
        Assert.Contains(nameof(ShellViewModel.CanChangeContext), seen);
    }

    // ------------------------------------------------------------------ switching branch

    [Fact]
    public async Task Nothing_running_means_nothing_is_asked()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        await fixture.Shell.SwitchBranchAsync(fixture.Other);

        Assert.Empty(fixture.Asked);
    }

    [Fact]
    public async Task Backing_out_of_the_question_puts_the_dropdown_back()
    {
        // The bug this whole area had: a switch that did not happen left the control showing a
        // branch the user was not on, and every other panel disagreed with it silently.
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        await fixture.Shell.SwitchBranchAsync(fixture.Other);

        Assert.Single(fixture.Asked);
        Assert.Equal(fixture.Current, fixture.Shell.SelectedBranch);
    }

    [Fact]
    public async Task An_import_refuses_the_switch_and_puts_the_dropdown_back()
    {
        // Unreachable through the UI, since the control is disabled — but the guard is the
        // authority, not the control's enabled state, and a keyboard or a binding glitch must
        // not be a way round it.
        var fixture = Fixture.Build(GuardChoice.Proceed);

        using var importing = fixture.Shell.Busy.Begin(WorkKind.Import, "Importing…");

        await fixture.Shell.SwitchBranchAsync(fixture.Other);

        var prompt = Assert.Single(fixture.Asked);

        Assert.Empty(prompt.Choices);
        Assert.Equal(fixture.Current, fixture.Shell.SelectedBranch);
    }

    [Fact]
    public async Task Choosing_to_stop_the_export_cancels_it_before_switching()
    {
        var fixture = Fixture.Build(GuardChoice.CancelWorkAndProceed);

        var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        var switching = fixture.Shell.SwitchBranchAsync(fixture.Other);

        Assert.True(exporting.Token.IsCancellationRequested);

        // The switch waits for the run to actually unwind, not merely for the token to trip.
        Assert.False(switching.IsCompleted);

        exporting.Dispose();
        await switching;
    }

    [Fact]
    public async Task Choosing_to_wait_leaves_the_export_alone_until_it_finishes()
    {
        var fixture = Fixture.Build(GuardChoice.WaitThenProceed);

        var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        var switching = fixture.Shell.SwitchBranchAsync(fixture.Other);

        Assert.False(exporting.Token.IsCancellationRequested);
        Assert.False(switching.IsCompleted);

        exporting.Dispose();
        await switching;
    }

    [Fact]
    public async Task The_question_names_the_work_that_is_in_the_way()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        await fixture.Shell.SwitchBranchAsync(fixture.Other);

        var prompt = Assert.Single(fixture.Asked);

        Assert.Contains("Exporting Machine…", prompt.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dirty_tree_is_asked_about_now_that_it_can_be_stashed()
    {
        // INVERTED, deliberately. This asserted the opposite while the auto-stash did not
        // exist: offering "stash my changes" before anything could stash would have been a
        // button that lies about what it does, which is how somebody loses work believing it
        // was saved. The stash exists now, so the tree goes to the guard and the offer is real.
        var fixture = Fixture.Build(GuardChoice.Cancel);

        fixture.Shell.Changes.Add(new ChangeRow(new FileChange
        {
            Path = "plc/Machine.st",
            Staged = ChangeKind.Unmodified,
            Unstaged = ChangeKind.Modified,
        }));

        await fixture.Shell.SwitchBranchAsync(fixture.Other);

        var prompt = Assert.Single(fixture.Asked);
        Assert.Contains(GuardChoice.StashAndProceed, prompt.Choices);

        // Cancel was the answer, so the dropdown is back on the branch we are actually on.
        Assert.True(fixture.Shell.SelectedBranch?.IsCurrent);
    }

    // -------------------------------------------------------------- switching repository

    [Fact]
    public async Task A_repository_switch_asks_about_a_running_export_too()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        await fixture.Shell.SwitchRepositoryAsync(new RepoEntry(@"C:\nowhere\other-repo"));

        Assert.Single(fixture.Asked);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_opened_leaves_the_dropdown_where_it_was()
    {
        // The dropdown must not be left pointing at a repository that did not open.
        //
        // What it is ASKED has changed. Every row in that control is a repository the user
        // was told they could come back to, so one that has since vanished gets the question
        // about a missing repository - clone it again, remove it, leave it alone - rather
        // than the generic "not a Git repository" and a folder picker, which is a strange
        // answer to a row the app itself put there. RepositoryRejected still belongs to the
        // picker, where the folder is one the user has just chosen.
        var fixture = Fixture.Build(GuardChoice.Proceed);
        var rejected = 0;
        var asked = 0;

        fixture.Shell.RepositoryRejected += _ => rejected++;
        fixture.Shell.Asked().MissingRepository = _ =>
        {
            asked++;
            return MissingRepositoryChoice.Cancel;
        };

        await fixture.Shell.SwitchRepositoryAsync(new RepoEntry(@"C:\nowhere\not-a-repo"));

        Assert.Equal(1, asked);
        Assert.Equal(0, rejected);
        Assert.Null(fixture.Shell.SelectedRepository);
    }
}
