using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Guarding;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Reconciling with the upstream is protected the way a named merge is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two buttons, one hazard, one guard until now.</b> A named merge is asked what it would
/// bring, and when that reaches a tracked project the result is not adopted: the mirror is put
/// in charge and an import is raised, because the binary project keeps one side while the text
/// keeps both (<c>MergePreflight</c>'s measurement). "Merge upstream" did the same merge through
/// <c>WhileGitRewritesTheTreeAsync</c> with the adopting default, so the disagreement was
/// recorded as synced: the engine answered NoChange, and the next project save exported over
/// the logic the upstream had brought (the 2026-10-25 review's fourth round, item 2). Rebase
/// replays this branch onto the same upstream and arrives at the same tree, so it is guarded
/// the same way.
/// </para>
/// <para>
/// Against a real repository and a real remote, as <c>ConflictFlowTests</c> is, because what is
/// being checked is which side of a merge lands where.
/// </para>
/// </remarks>
public sealed class UpstreamMergeProtectionTests
{
    private static ShellViewModel Shell()
    {
        var shell = TestShell.Build();

        // The import warning is a destructive prompt; this suite says yes to it. The decline
        // path is the named merge's and is covered there. The tree-rewrite guard asks too,
        // because a tracked project is present, and an unanswered guard cancels the merge
        // without a word — which would make every assertion below fail for the wrong reason.
        shell.Asked().Destructive = _ => true;
        shell.Asked().Guard = _ => GuardChoice.Proceed;
        return shell;
    }

    /// <summary>
    /// A tracked project whose upstream edited the mirror while this branch did other work, so
    /// the branches have diverged and the merge is clean.
    /// </summary>
    private static TempRepo DivergedOn(string upstreamEdits)
    {
        var repo = new TempRepo(published: true);
        ImportConfirmationTests.GivenATrackedProject(repo);
        repo.Git("push -q");

        // The remote moves first, in a second clone — the same shape as ConflictFlowTests.
        var other = Path.Combine(Path.GetTempPath(), "versio-other-" + Guid.NewGuid().ToString("N")[..8]);
        TempRepo.Git(Path.GetTempPath(), $"clone -q \"{repo.RemotePath}\" \"{other}\"");
        TempRepo.Git(other, "config user.name Other");
        TempRepo.Git(other, "config user.email other@versio.local");
        File.AppendAllText(Path.Combine(other, upstreamEdits.Replace('/', Path.DirectorySeparatorChar)),
            "// upstream logic\n");
        TempRepo.Git(other, "commit -qam upstream");
        TempRepo.Git(other, "push -q");
        TempRepo.DeleteTree(other);

        // And this branch commits something else, so neither side can fast-forward.
        repo.Touch("notes.md");
        repo.Git("add .");
        repo.Git("commit -qm mine");
        repo.Git("fetch -q");
        return repo;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciling_with_an_upstream_that_edited_the_mirror_leaves_the_text_in_charge(bool rebase)
    {
        using var repo = DivergedOn("plc/Machine/App/FB_Motor.st");
        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        var projectBefore = await File.ReadAllBytesAsync(ImportConfirmationTests.ProjectPath(repo));

        await Eventually.Press(shell.ReconcileCommand, rebase, rebase ? "rebase" : "merge");

        // The mirror holds the upstream's logic and the project does not. Recording that as
        // synced is what let the next save export over it; an import is what puts it back.
        var mirror = Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st");
        await Eventually.Until(
            () => File.ReadAllText(mirror).Contains("// upstream logic", StringComparison.Ordinal),
            $"the {(rebase ? "rebase" : "merge")} landed");
        Assert.Equal(projectBefore, await File.ReadAllBytesAsync(ImportConfirmationTests.ProjectPath(repo)));

        await Eventually.Until(() => shell.Sync.HasPendingImport, "the merged text was not put in charge");
        Assert.Equal("Machine", shell.Sync.PendingImportProject?.Name);
    }

    [Fact]
    public async Task Reconciling_with_an_upstream_that_touched_no_tracked_project_adopts_as_before()
    {
        // The other direction: an upstream that changed nothing Versio tracks is an ordinary
        // merge, adopted like a pull, and no import is owed.
        using var repo = DivergedOn("seed.txt");
        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.ReconcileCommand, false, "merge");
        await Eventually.Until(() => !shell.IsMidMerge && shell.Conflicted.Count == 0, "the merge finished");

        Assert.Contains("// upstream logic", await File.ReadAllTextAsync(Path.Combine(repo.Path, "seed.txt")));
        Assert.False(shell.Sync.HasPendingImport, "an import was raised over a merge that reached no tracked project");
    }
}
