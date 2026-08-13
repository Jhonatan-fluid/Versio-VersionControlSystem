using System.IO;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Switching repository forgets the one being left.
/// </summary>
/// <remarks>
/// <para>
/// Opening a repository called <c>Sync.AttachAsync</c> and never <c>Sync.Detach</c>, so the
/// project panel kept the previous repository's expanded folders, coverage, staleness and
/// search, and the shell kept its diff, its commit message and its branch list. Some of it was
/// corrected a moment later by the refresh, which is what made the rest read as a glitch rather
/// than a bug.
/// </para>
/// <para>
/// The <c>Detach</c> method already knew what to forget and said so in a comment describing
/// exactly this failure — "'docs' is open in both… would open folders in the new repository
/// because of a click in the old one". Nothing called it.
/// </para>
/// </remarks>
public sealed class RepositoryChangeResetTests
{
    private const string RepoA = @"C:\work\alpha";
    private const string RepoB = @"C:\work\beta";

    private static ShellViewModel Build()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        // No toolchain. Adoption exports a project the moment it is tracked, which is right
        // for a user and ruinous here: these tests drop a .project into a temp repository, and
        // without this they launch the real CODESYS and wait for it to fail — one of them went
        // from one second to eight. A repository on a machine with no toolchain is a state the
        // app already handles and reports, so this exercises a real path rather than a stub.
        return new ShellViewModel(
            logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    /// <summary>Fill the shell with everything a repository puts on screen.</summary>
    private static void Populate(ShellViewModel shell)
    {
        shell.Changes.Add(Row("plc/Machine.st"));
        shell.Staged.Add(Row("plc/Other.st"));
        shell.Unstaged.Add(Row("plc/Third.st"));

        shell.Branches.Add(new BranchEntry(new BranchInfo { Name = "main", IsCurrent = true }));
        shell.Branches.Add(new BranchEntry(new BranchInfo { Name = "wip", IsCurrent = false }));

        shell.CommitMessage = "Half a thought about the pump interlock";
        shell.ShowFullFile = true;
        shell.Sync.TreeSearch = "interlock";
    }

    private static ChangeRow Row(string path) => new(new FileChange
    {
        Path = path,
        Staged = ChangeKind.Unmodified,
        Unstaged = ChangeKind.Modified,
    });

    [Fact]
    public void Everything_the_old_repository_filled_is_cleared()
    {
        var shell = Build();
        Populate(shell);

        shell.ResetForRepositoryChange(RepoA, RepoB);

        Assert.Empty(shell.Changes);
        Assert.Empty(shell.Staged);
        Assert.Empty(shell.Unstaged);
        Assert.Empty(shell.Branches);
        Assert.Null(shell.SelectedBranch);
        Assert.Null(shell.SelectedStaged);
        Assert.Null(shell.SelectedUnstaged);
        Assert.Equal("—", shell.BranchName);
        Assert.Equal(string.Empty, shell.AheadBehind);
        Assert.Equal(string.Empty, shell.SelectedDiff);
        Assert.Equal(string.Empty, shell.SelectedDiffPath);
        Assert.False(shell.ShowFullFile);
        Assert.Equal(string.Empty, shell.LastRefreshText);
    }

    [Fact]
    public void The_project_panel_forgets_its_repository_too()
    {
        // The half that already had a method for it and never had a caller.
        var shell = Build();
        Populate(shell);

        shell.ResetForRepositoryChange(RepoA, RepoB);

        Assert.Empty(shell.Sync.Projects);
        Assert.Empty(shell.Sync.Findings);
        Assert.Empty(shell.Sync.Tree);
        Assert.Equal(string.Empty, shell.Sync.TreeSearch);
        Assert.Null(shell.Sync.SelectedProject);
        Assert.Null(shell.Sync.FailedProject);
        Assert.False(shell.Sync.HasFailure);
        Assert.Empty(shell.Sync.HeldLocks);
        Assert.Equal(0, shell.Sync.UnexportedCount);
    }

    // ------------------------------------------------------------------- the typed message

    [Fact]
    public void A_message_typed_for_one_repository_does_not_appear_in_another()
    {
        var shell = Build();
        shell.CommitMessage = "Half a thought about the pump interlock";

        shell.ResetForRepositoryChange(RepoA, RepoB);

        Assert.Equal(string.Empty, shell.CommitMessage);
    }

    [Fact]
    public void Coming_back_brings_the_message_with_you()
    {
        // Silently binning somebody's typing is the thing they actually notice. Kept in memory
        // rather than in repo.json, which is committed and shared - a draft must not be pushed.
        var shell = Build();
        shell.CommitMessage = "Half a thought about the pump interlock";

        shell.ResetForRepositoryChange(RepoA, RepoB);
        shell.CommitMessage = "something else entirely";
        shell.ResetForRepositoryChange(RepoB, RepoA);

        Assert.Equal("Half a thought about the pump interlock", shell.CommitMessage);
    }

    [Fact]
    public void Each_repository_keeps_its_own_message()
    {
        var shell = Build();

        shell.CommitMessage = "alpha's message";
        shell.ResetForRepositoryChange(RepoA, RepoB);

        shell.CommitMessage = "beta's message";
        shell.ResetForRepositoryChange(RepoB, RepoA);

        Assert.Equal("alpha's message", shell.CommitMessage);

        shell.ResetForRepositoryChange(RepoA, RepoB);

        Assert.Equal("beta's message", shell.CommitMessage);
    }

    [Fact]
    public void An_emptied_message_is_not_remembered()
    {
        // Clearing the box is a decision. Handing it back would be undoing it.
        var shell = Build();

        shell.CommitMessage = "typed, then thought better of";
        shell.ResetForRepositoryChange(RepoA, RepoB);
        shell.ResetForRepositoryChange(RepoB, RepoA);

        Assert.Equal("typed, then thought better of", shell.CommitMessage);

        shell.CommitMessage = "   ";
        shell.ResetForRepositoryChange(RepoA, RepoB);
        shell.ResetForRepositoryChange(RepoB, RepoA);

        Assert.Equal(string.Empty, shell.CommitMessage);
    }

    // ------------------------------------------------------- a folder that is not taken up

    [Fact]
    public async Task A_rejected_folder_costs_nothing_that_was_on_screen()
    {
        // The reset runs only once the switch is certain. Picking the wrong directory must not
        // punish a misclick by throwing away the diff and the message being typed.
        var shell = Build();
        var rejected = 0;

        shell.RepositoryRejected += _ => rejected++;
        Populate(shell);

        await shell.OpenAsync(@"C:\nowhere\definitely-not-a-repository");

        Assert.Equal(1, rejected);
        Assert.Equal("Half a thought about the pump interlock", shell.CommitMessage);
        Assert.NotEmpty(shell.Changes);
        Assert.NotEmpty(shell.Branches);
        Assert.Equal("interlock", shell.Sync.TreeSearch);
    }

    // ---------------------------------------------------------------- proving it is CALLED

    /// <summary>A throwaway git repository, deleted when the test ends.</summary>
    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "versio-reset-" + Guid.NewGuid().ToString("N")[..8]);

        public TempRepo(string fileName)
        {
            Directory.CreateDirectory(Path);
            Run("init -q .");
            Run("config user.email test@versio");
            Run("config user.name test");
            File.WriteAllText(System.IO.Path.Combine(Path, fileName), "contents");
            Run("add .");
            Run("commit -qm first");
        }

        private void Run(string arguments)
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            git!.WaitForExit();
        }

        public void Dispose()
        {
            try
            {
                // .git holds read-only objects on Windows, which Delete refuses to remove.
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    private static bool GitIsAvailable()
    {
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            git!.WaitForExit();
            return git.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task Opening_another_repository_actually_performs_the_reset()
    {
        // THE TEST THAT MATTERS, and the one that was missing.
        //
        // Every other test here calls ResetForRepositoryChange directly, which proves the
        // method works and says nothing about whether opening a repository calls it. Deleting
        // the call from OpenRepositoryAsync left all of them green — the same shape of hole as
        // guard intents that were defined and never asked.
        //
        // The only way to close it is to open something real.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var alpha = new TempRepo("alpha.txt");
        using var beta = new TempRepo("beta.txt");

        var shell = Build();

        await shell.OpenAsync(alpha.Path);

        shell.CommitMessage = "typed against alpha";
        shell.Sync.TreeSearch = "interlock";

        await shell.OpenAsync(beta.Path);

        Assert.Equal(beta.Path, shell.RepositoryPath);
        Assert.Equal(string.Empty, shell.CommitMessage);
        Assert.Equal(string.Empty, shell.Sync.TreeSearch);

        // And back again: the draft belongs to alpha and comes with it.
        await shell.OpenAsync(alpha.Path);

        Assert.Equal("typed against alpha", shell.CommitMessage);

        shell.Dispose();
    }

    // ------------------------------------------------------------------ bound to real controls

    [Fact]
    public void Clearing_the_bound_collections_is_not_read_as_a_branch_switch()
    {
        // Both dropdowns are bound with a selection. Emptying ItemsSource raises a selection
        // change, and without the suppression that is indistinguishable from the user picking
        // something - which would start a checkout in the middle of tearing the panel down.
        //
        // Only a real window has the bindings, so this is asserted with one attached rather
        // than against the view model alone.
        StaHarness.Run(() =>
        {
            var shell = Build();
            var window = new MainWindow(shell);

            Populate(shell);
            window.Show();

            shell.ResetForRepositoryChange(RepoA, RepoB);

            Assert.Empty(shell.Branches);
            Assert.Null(shell.SelectedBranch);
            Assert.Equal(string.Empty, shell.CommitMessage);

            window.Close();
            window.Dispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    [SkippableFact]
    public async Task A_project_dropped_in_while_running_is_noticed()
    {
        // Reported from the running application: a repository was created, a .project added,
        // and it sat in the changed-file list as untracked for ever. The project scan ran only
        // when a repository was opened, after a sync did work, and after a push, discard or
        // retry — never because a file appeared. Git noticed it instantly, which is what made
        // it look as though Versio had seen it and decided to do nothing.
        //
        // The watcher could not have covered it: it follows the project and mirror of each
        // EXISTING binding, and in a repository with none it does not start at all — exactly
        // the state somebody is in when they add their first project.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo("readme.txt");

        var shell = Build();

        await shell.OpenAsync(repo.Path);

        Assert.Empty(shell.Sync.Projects);

        File.WriteAllText(
            System.IO.Path.Combine(repo.Path, "Machine(Extended).project"), "not really a project");

        await shell.RefreshAsync();

        Assert.Contains(shell.Sync.Projects, p => p.Path == "Machine(Extended).project");

        shell.Dispose();
    }

    [SkippableFact]
    public async Task An_export_landing_mid_review_updates_the_tree_rather_than_replacing_it()
    {
        // THE WIRING, not the algorithm.
        //
        // TreeReconcileTests proves TreeRow.Reconcile does the right thing when called. It says
        // nothing about whether RefreshTreeAsync calls it — and reverting that one line to
        // Tree.Clear() plus a refill left all of those tests green, which is the third time in
        // this work that a unit test proved a method and not its caller.
        //
        // Selection is a reference to the row. If the refresh replaces row objects, whoever was
        // reading the tree loses their place every time a save triggers an export.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo("readme.txt");

        var shell = Build();

        await shell.OpenAsync(repo.Path);

        File.WriteAllText(
            System.IO.Path.Combine(repo.Path, "Machine.project"), "a tracked project");

        await shell.RefreshAsync();

        // Adoption also writes .gitattributes, .gitignore and .versio/repo.json, so the tree
        // holds those alongside the project. The project row is the one being read.
        var reading = Assert.Single(shell.Sync.Tree, r => r.IsProject);
        var before = shell.Sync.Tree.Count;

        // Something else changes, so the tree's signature moves and it genuinely rebuilds.
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "notes.txt"), "and another file");

        await shell.RefreshAsync();

        Assert.True(shell.Sync.Tree.Count > before, "the refresh did not actually rebuild");
        Assert.Same(reading, Assert.Single(shell.Sync.Tree, r => r.IsProject));

        shell.Dispose();
    }

    [SkippableFact]
    public void Two_real_repositories_swapped_under_a_real_window()
    {
        // As close to the application as automation gets without a mouse: real git, real
        // repository locator, real git client, real Detach and Attach, and a real MainWindow
        // with its bindings live throughout.
        //
        // The two halves are covered separately elsewhere — one test opens real repositories
        // with no window, another clears bound collections with no git. Neither would notice a
        // failure that needs both, and "clearing ItemsSource mid-switch trips the selection
        // handler" is exactly that shape.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var alpha = new TempRepo("alpha.txt");
        using var beta = new TempRepo("beta.txt");

        StaHarness.Run(async () =>
        {
            var shell = Build();
            var window = new MainWindow(shell);

            window.Show();

            await shell.OpenAsync(alpha.Path);

            shell.CommitMessage = "typed against alpha";

            Assert.Equal(alpha.Path, shell.RepositoryPath);
            Assert.NotEmpty(shell.Branches);

            await shell.OpenAsync(beta.Path);

            Assert.Equal(beta.Path, shell.RepositoryPath);
            Assert.Equal(string.Empty, shell.CommitMessage);

            // The branch list is beta's, and the dropdown is on a branch beta actually has.
            Assert.NotEmpty(shell.Branches);
            Assert.NotNull(shell.SelectedBranch);
            Assert.True(shell.SelectedBranch!.IsCurrent);

            await shell.OpenAsync(alpha.Path);

            Assert.Equal("typed against alpha", shell.CommitMessage);

            shell.Dispose();
            window.Close();
            window.Dispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }
}
