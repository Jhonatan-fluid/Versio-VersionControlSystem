using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A real conflict, and the way out of a real divergence.
/// </summary>
/// <remarks>
/// <para>
/// Every conflict here is made by git, by merging two branches that changed the same line.
/// Nothing about a merge is worth faking: which column an unmerged path appears in, what
/// <c>checkout --ours</c> leaves in the index, and which marker file git writes are all
/// details this code reads and none of them is guessable.
/// </para>
/// <para>
/// The divergence tests are the ones that close the panel's last dead end. Before them the
/// badge said "diverged — pull first" and <c>pull --ff-only</c> refuses a diverged branch by
/// design, so the advice pointed at a button that could not work.
/// </para>
/// </remarks>
public sealed class ConflictFlowTests
{
    private static ShellViewModel Shell()
    {
        using var shell = TestShell.Build();

        // Every destructive prompt is answered yes. What is under test is the operation,
        // not the dialog — the refusal path has its own test below.
        shell.Asked().Destructive = _ => true;

        return shell;
    }

    /// <summary>A repository whose branch conflicts with its upstream on one line.</summary>
    private static TempRepo Diverged()
    {
        var repo = new TempRepo(published: true);

        // The remote moves first, in a second clone.
        var other = Path.Combine(Path.GetTempPath(), "versio-other-" + Guid.NewGuid().ToString("N")[..8]);
        TempRepo.Git(Path.GetTempPath(), $"clone -q \"{repo.RemotePath}\" \"{other}\"");
        TempRepo.Git(other, "config user.name Other");
        TempRepo.Git(other, "config user.email other@versio.local");
        File.WriteAllText(Path.Combine(other, "seed.txt"), "theirs\n");
        TempRepo.Git(other, "commit -qam theirs");
        TempRepo.Git(other, "push -q");

        // The clone was only needed to move the remote. Deleted through TempRepo's helper
        // because git leaves its objects read-only and a plain delete throws.
        TempRepo.DeleteTree(other);

        // And this branch changes the same line, so neither can fast-forward.
        File.WriteAllText(Path.Combine(repo.Path, "seed.txt"), "mine\n");
        repo.Git("commit -qam mine");
        repo.Git("fetch -q");

        return repo;
    }

    [Fact]
    public async Task A_diverged_branch_is_offered_a_way_out()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(TrackingState.Diverged, shell.Remote.Standing.State);
        Assert.True(shell.Remote.Standing.IsDiverged);

        // The dead end this closes: the badge says pull first, and --ff-only refuses.
        Assert.True(shell.ReconcileCommand.CanExecute(true));
        Assert.True(shell.ReconcileCommand.CanExecute(false));
    }

    [Fact]
    public async Task Nothing_is_offered_when_the_branch_has_not_diverged()
    {
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        // Everywhere else the choice is noise, and --ff-only is the right default precisely
        // because auto-merging native CODESYS XML produces a file nobody reviewed.
        Assert.False(shell.Remote.Standing.IsDiverged);
        Assert.False(shell.ReconcileCommand.CanExecute(true));
    }

    [Fact]
    public async Task A_merge_that_conflicts_stops_and_shows_the_files()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped at a conflict");

        Assert.Equal(MergeState.Merging, shell.MergeState);
        Assert.True(shell.IsMidMerge);
        Assert.Contains("seed.txt", shell.Conflicted.Select(r => r.Path));
        Assert.Contains("conflict", shell.ConflictHeadline);
    }

    [Fact]
    public async Task A_conflicted_file_appears_in_its_own_section_and_no_other()
    {
        // An unmerged path is reported in BOTH of git's columns. Listing it in all three
        // sections would be three rows for one problem, and neither of the other two describes
        // what git will actually do with it.
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped");

        Assert.DoesNotContain("seed.txt", shell.Staged.Select(r => r.Path));
        Assert.DoesNotContain("seed.txt", shell.Unstaged.Select(r => r.Path));
    }

    [Fact]
    public async Task Committing_is_refused_while_anything_is_unmerged()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped");

        shell.CommitMessage = "trying anyway";

        // git refuses this regardless; a button that explains beats one that fails.
        Assert.True(shell.HasConflicts);
        Assert.False(shell.CommitCommand.CanExecute(null));
        Assert.Equal("Resolve the conflicts first.", shell.CommitBlockedReason);
    }

    [Fact]
    public async Task Taking_one_side_resolves_the_file()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped");

        shell.SelectedConflict = shell.Conflicted[0];

        await Eventually.Press(shell.ResolveCommand, true, "keep mine");
        await Eventually.Until(() => shell.Conflicted.Count == 0, "the file was resolved");

        // Resolved, meaning the index entry is no longer unmerged. checkout --ours writes the
        // working-tree file and leaves the index conflicted, which is the state that makes
        // somebody think the button did nothing - so the `add` afterwards is the real fix.
        //
        // NOT asserted to appear in Staged, and that asymmetry is git being right rather than
        // a gap: taking "mine" restores exactly what HEAD already holds, so the index matches
        // HEAD and there is no change to report. Taking "theirs" would show one.
        Assert.False(shell.HasConflicts);
        Assert.Empty(shell.Conflicted);

        // The merge is still in progress: the conflicts are gone, the commit that concludes it
        // has not happened. Reading "no conflicts" as "nothing happening" would be wrong here.
        Assert.True(shell.IsMidMerge);
        Assert.Contains("Commit to finish", shell.ConflictHeadline);
    }

    [Fact]
    public async Task Keeping_mine_keeps_this_branch_version()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped");

        shell.SelectedConflict = shell.Conflicted[0];

        await Eventually.Press(shell.ResolveCommand, true, "keep mine");
        await Eventually.Until(() => shell.Conflicted.Count == 0, "the file was resolved");

        // Asserted on the file, because "ours" and "theirs" are the two things easiest to
        // wire the wrong way round and nothing else would notice.
        Assert.Equal("mine\n",
                     File.ReadAllText(Path.Combine(repo.Path, "seed.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Keeping_theirs_takes_the_incoming_version()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped");

        shell.SelectedConflict = shell.Conflicted[0];

        await Eventually.Press(shell.ResolveCommand, false, "keep theirs");
        await Eventually.Until(() => shell.Conflicted.Count == 0, "the file was resolved");

        Assert.Equal("theirs\n",
                     File.ReadAllText(Path.Combine(repo.Path, "seed.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Abandoning_puts_the_branch_back()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped");

        await Eventually.Press(shell.AbortMergeCommand, "abandon");
        await Eventually.Until(() => !shell.IsMidMerge, "the merge was abandoned");

        // Offering an operation that can stop half-done without offering the way back is how
        // somebody ends up in a state they cannot describe.
        Assert.Empty(shell.Conflicted);
        Assert.Equal(TrackingState.Diverged, shell.Remote.Standing.State);
        Assert.Equal("mine\n",
                     File.ReadAllText(Path.Combine(repo.Path, "seed.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Abandoning_can_be_declined()
    {
        using var repo = Diverged();

        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        using var shell = new ShellViewModel(
            logger, new LogViewModel(sink), toolchains: (_, _) => null);
        shell.Asked().Destructive = _ => false;

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => shell.Conflicted.Count > 0, "the merge stopped");

        await Eventually.Press(shell.AbortMergeCommand, "abandon");
        await Task.Delay(500);

        // It throws away conflict resolutions somebody may have spent real time on, and unlike
        // a discard what it destroys is not visible as a list of files.
        Assert.True(shell.IsMidMerge);
    }

    [Fact]
    public async Task A_rebase_that_conflicts_says_rebase_rather_than_merge()
    {
        using var repo = Diverged();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.ReconcileCommand, true, "rebase");
        await Eventually.Until(() => shell.IsMidMerge, "the rebase stopped at a conflict");

        // The two are different states with different marker files, and the wording follows
        // git rather than guessing from the presence of conflicts.
        Assert.Equal(MergeState.Rebasing, shell.MergeState);
        Assert.Contains("rebase", shell.ConflictHeadline);
    }

    [Fact]
    public async Task Nothing_is_mid_merge_in_an_ordinary_repository()
    {
        using var repo = new TempRepo(unstagedFiles: 2);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.False(shell.IsMidMerge);
        Assert.Empty(shell.Conflicted);
        Assert.Equal(string.Empty, shell.ConflictHeadline);
        Assert.False(shell.AbortMergeCommand.CanExecute(null));
    }
}
