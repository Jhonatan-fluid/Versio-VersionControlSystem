using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Where the shell writes the per-clone exclude rule for <c>.versio/local/</c>.
/// </summary>
/// <remarks>
/// The rule itself is planned in Core and written by <c>LocalExclude</c>; what this holds
/// still is which directory the shell hands it. That choice is one line in <c>OpenAsync</c>,
/// nothing on screen depends on it, and getting it wrong is silent until the next auto-stash
/// sweeps its own record — the 2026-10-24 review's item 25.
/// </remarks>
public sealed class LocalExcludeFlowTests
{
    [Fact]
    public async Task Opening_a_linked_worktree_excludes_the_scratch_directory_where_git_reads_it()
    {
        // git reads info/exclude from the common directory only. The shell used to pass the
        // per-worktree directory (.git/worktrees/<name>), where the rule was written and
        // never read, so .versio/local/ stayed untracked in every linked worktree.
        using var repo = new TempRepo();
        repo.Git("worktree add -q wt");
        var worktree = Path.Combine(repo.Path, "wt");

        using var shell = TestShell.Build();
        await shell.OpenAsync(worktree);

        var common = Path.Combine(repo.Path, ".git", "info", "exclude");
        var perWorktree = Path.Combine(repo.Path, ".git", "worktrees", "wt", "info", "exclude");

        Assert.True(File.Exists(common), "no exclude was written under the common directory");
        Assert.Contains(".versio/local/", File.ReadAllText(common), StringComparison.Ordinal);
        Assert.False(File.Exists(perWorktree),
                     "the rule was written under the per-worktree directory, where git does not read it");
    }
}
