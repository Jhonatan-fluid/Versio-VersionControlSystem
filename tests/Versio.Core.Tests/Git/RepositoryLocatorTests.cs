using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Versio is built entirely on Git, so refusing a non-repository cleanly is a feature, not
/// an error path. Every case here produces a message a user can act on.
/// </summary>
public class RepositoryLocatorTests
{
    /// <summary>In-memory filesystem shaped for the walk.</summary>
    private sealed class Probe : IRepositoryProbe
    {
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public Probe AddDir(params string[] paths)
        {
            foreach (var p in paths)
            {
                _dirs.Add(p);
            }

            return this;
        }

        public Probe AddFile(string path, string firstLine = "")
        {
            _files[path] = firstLine;
            return this;
        }

        public bool DirectoryExists(string path) => _dirs.Contains(path);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public string? ReadFirstLine(string path) =>
            _files.TryGetValue(path, out var line) ? line : null;
    }

    [Fact]
    public void A_working_tree_root_is_recognised()
    {
        var probe = new Probe().AddDir(@"C:\repo", @"C:\repo\.git");

        var result = new RepositoryLocator(probe).Locate(@"C:\repo");

        Assert.Equal(RepositoryKind.WorkingTreeRoot, result.Kind);
        Assert.Equal(@"C:\repo", result.WorkingTreeRoot);
        Assert.True(result.IsUsable);
    }

    [Fact]
    public void A_subdirectory_resolves_to_the_repository_root()
    {
        // People select the folder they were looking at, not the root. Picking
        // repo/src/plc should open repo, exactly as `git status` would.
        var probe = new Probe().AddDir(
            @"C:\repo", @"C:\repo\.git", @"C:\repo\src", @"C:\repo\src\plc");

        var result = new RepositoryLocator(probe).Locate(@"C:\repo\src\plc");

        Assert.Equal(RepositoryKind.InsideWorkingTree, result.Kind);
        Assert.Equal(@"C:\repo", result.WorkingTreeRoot);
        Assert.True(result.IsUsable);
        Assert.Contains(@"C:\repo", result.Explanation);
    }

    [Fact]
    public void A_linked_worktree_is_recognised_from_its_pointer_file()
    {
        // A worktree or submodule stores .git as a FILE containing "gitdir: …". A naive
        // Directory.Exists(".git") check reports these as not-a-repository, and the user
        // is told to run git init inside a perfectly good checkout.
        var probe = new Probe()
            .AddDir(@"C:\wt")
            .AddFile(@"C:\wt\.git", "gitdir: C:/repo/.git/worktrees/wt");

        var result = new RepositoryLocator(probe).Locate(@"C:\wt");

        Assert.Equal(RepositoryKind.LinkedWorkingTree, result.Kind);
        Assert.Equal(@"C:\wt", result.WorkingTreeRoot);
        Assert.Equal("C:/repo/.git/worktrees/wt", result.GitPath);
        Assert.True(result.IsUsable);
    }

    [Fact]
    public void A_dot_git_file_without_a_gitdir_pointer_is_not_a_repository()
    {
        var probe = new Probe().AddDir(@"C:\x").AddFile(@"C:\x\.git", "just some text");

        Assert.Equal(RepositoryKind.NotARepository,
            new RepositoryLocator(probe).Locate(@"C:\x").Kind);
    }

    [Fact]
    public void A_bare_repository_is_reported_distinctly_and_refused()
    {
        // Real git data, but no working tree to diff against. Calling it "not a repository"
        // would be a lie, and the user would keep trying.
        var probe = new Probe()
            .AddDir(@"C:\repo.git", @"C:\repo.git\objects", @"C:\repo.git\refs")
            .AddFile(@"C:\repo.git\HEAD", "ref: refs/heads/main");

        var result = new RepositoryLocator(probe).Locate(@"C:\repo.git");

        Assert.Equal(RepositoryKind.Bare, result.Kind);
        Assert.False(result.IsUsable);
        Assert.Contains("bare", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no working tree", result.Explanation);
    }

    [Fact]
    public void A_plain_folder_is_refused_with_an_actionable_message()
    {
        var probe = new Probe().AddDir(@"C:\documents", @"C:\documents\stuff");

        var result = new RepositoryLocator(probe).Locate(@"C:\documents\stuff");

        Assert.Equal(RepositoryKind.NotARepository, result.Kind);
        Assert.False(result.IsUsable);
        Assert.Contains("git init", result.Explanation);
    }

    [Fact]
    public void A_missing_folder_is_inaccessible_rather_than_not_a_repository()
    {
        var result = new RepositoryLocator(new Probe()).Locate(@"C:\nope");

        Assert.Equal(RepositoryKind.Inaccessible, result.Kind);
        Assert.False(result.IsUsable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_inaccessible(string? path)
    {
        Assert.Equal(RepositoryKind.Inaccessible,
            new RepositoryLocator(new Probe()).Locate(path).Kind);
    }

    [Fact]
    public void A_trailing_separator_does_not_make_the_root_look_like_a_subdirectory()
    {
        var probe = new Probe().AddDir(@"C:\repo", @"C:\repo\.git");

        Assert.Equal(RepositoryKind.WorkingTreeRoot,
            new RepositoryLocator(probe).Locate(@"C:\repo\").Kind);
    }

    [Fact]
    public void The_nearest_repository_wins_for_a_nested_checkout()
    {
        // A submodule or a repo inside a repo: the inner one owns the file you selected.
        var probe = new Probe().AddDir(
            @"C:\outer", @"C:\outer\.git",
            @"C:\outer\inner", @"C:\outer\inner\.git",
            @"C:\outer\inner\src");

        var result = new RepositoryLocator(probe).Locate(@"C:\outer\inner\src");

        Assert.Equal(@"C:\outer\inner", result.WorkingTreeRoot);
    }

    [Fact]
    public void The_walk_stops_at_the_drive_root_rather_than_looping()
    {
        var probe = new Probe().AddDir(@"C:\", @"C:\a", @"C:\a\b", @"C:\a\b\c");

        var result = new RepositoryLocator(probe).Locate(@"C:\a\b\c");

        Assert.Equal(RepositoryKind.NotARepository, result.Kind);
    }

    [Fact]
    public void Every_outcome_produces_a_non_empty_explanation()
    {
        // The message is the whole point of refusing: an app that just does nothing is
        // indistinguishable from one that is broken.
        foreach (var kind in Enum.GetValues<RepositoryKind>())
        {
            var location = new RepositoryLocation { Kind = kind, RequestedPath = @"C:\x" };
            Assert.False(string.IsNullOrWhiteSpace(location.Explanation));
        }
    }
}
