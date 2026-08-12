using Versio.Core.Watching;
using Xunit;

namespace Versio.Core.Tests.Watching;

/// <summary>
/// The filter that decides whether a filesystem event is worth a <c>git status</c>.
/// </summary>
/// <remarks>
/// Both failure directions are bugs and neither is visible without tests: filter too much
/// and the panel silently goes stale, filter too little and the app spawns git in a loop.
/// </remarks>
public class RepositoryChangeFilterTests
{
    private const string Root = @"C:\repo";

    private static RepositoryChangeRelevance Classify(string path) =>
        RepositoryChangeFilter.Classify(Root, path);

    [Theory]
    [InlineData(@"C:\repo\src\Program.cs")]
    [InlineData(@"C:\repo\README.md")]
    [InlineData(@"C:\repo\plc\Machine.project")]
    public void An_ordinary_file_is_a_working_tree_change(string path)
    {
        Assert.Equal(RepositoryChangeRelevance.WorkingTree, Classify(path));
    }

    [Theory]
    [InlineData(@"C:\repo\bin\Debug\app.dll")]
    [InlineData(@"C:\repo\obj\project.assets.json")]
    [InlineData(@"C:\repo\node_modules\left-pad\index.js")]
    public void Build_output_is_NOT_filtered_by_name(string path)
    {
        // Whether these are ignored is a property of the repository's .gitignore, not of
        // their names. Some repositories genuinely track a bin/ folder, and guessing would
        // reintroduce the staleness this whole feature exists to fix. Volume is handled by
        // debouncing instead: one build settles into one refresh.
        Assert.Equal(RepositoryChangeRelevance.WorkingTree, Classify(path));
    }

    [Theory]
    [InlineData(@"C:\repo\.git\index")]
    [InlineData(@"C:\repo\.git\HEAD")]
    [InlineData(@"C:\repo\.git\ORIG_HEAD")]
    [InlineData(@"C:\repo\.git\packed-refs")]
    public void Git_state_files_are_metadata_changes(string path)
    {
        // Someone staged, committed or checked out in a terminal. The branch name and the
        // ahead/behind counts move with it, so this refreshes more than the file list.
        Assert.Equal(RepositoryChangeRelevance.GitMetadata, Classify(path));
    }

    [Theory]
    [InlineData(@"C:\repo\.git\MERGE_HEAD")]
    [InlineData(@"C:\repo\.git\CHERRY_PICK_HEAD")]
    [InlineData(@"C:\repo\.git\REVERT_HEAD")]
    [InlineData(@"C:\repo\.git\REBASE_HEAD")]
    public void In_progress_operation_markers_are_metadata_changes(string path)
    {
        // These appear and vanish as a merge or rebase proceeds, and each transition changes
        // whether files are reported as conflicted.
        Assert.Equal(RepositoryChangeRelevance.GitMetadata, Classify(path));
    }

    [Theory]
    [InlineData(@"C:\repo\.git\refs\heads\main")]
    [InlineData(@"C:\repo\.git\refs\remotes\origin\main")]
    [InlineData(@"C:\repo\.git\refs\tags\v1.0")]
    public void Ref_updates_are_metadata_changes(string path)
    {
        Assert.Equal(RepositoryChangeRelevance.GitMetadata, Classify(path));
    }

    [Theory]
    [InlineData(@"C:\repo\.git\index.lock")]
    [InlineData(@"C:\repo\.git\refs\heads\main.lock")]
    public void Lock_files_are_ignored(string path)
    {
        // A lock file is created and deleted around every git invocation - including the
        // status calls this app makes itself. Following them is a treadmill that never idles.
        Assert.Equal(RepositoryChangeRelevance.Ignored, Classify(path));
    }

    [Theory]
    [InlineData(@"C:\repo\.git\objects\ab\cdef0123456789")]
    [InlineData(@"C:\repo\.git\logs\HEAD")]
    [InlineData(@"C:\repo\.git\COMMIT_EDITMSG")]
    [InlineData(@"C:\repo\.git\config")]
    public void Git_internals_that_do_not_move_the_status_are_ignored(string path)
    {
        // A fetch writes thousands of object files. None of them change what the user sees
        // until a ref moves, and refs are handled separately.
        Assert.Equal(RepositoryChangeRelevance.Ignored, Classify(path));
    }

    [Fact]
    public void A_bare_dot_git_event_is_ignored()
    {
        // Windows raises a directory-level event for .git every time anything inside it is
        // written - including the lock files git creates around every invocation. Treating
        // it as a metadata change made lock-file churn alone wake the watcher, which the
        // integration suite caught. Nothing is lost: a write to .git/index also raises an
        // event naming .git/index, and that one is matched.
        Assert.Equal(RepositoryChangeRelevance.Ignored, Classify(@"C:\repo\.git"));
    }

    [Fact]
    public void A_path_outside_the_repository_is_ignored()
    {
        // A junction or symlinked subfolder can produce one, and acting on it would refresh
        // this panel because of a file in some unrelated tree.
        Assert.Equal(RepositoryChangeRelevance.Ignored, Classify(@"C:\elsewhere\file.cs"));
    }

    [Fact]
    public void A_sibling_directory_with_a_shared_prefix_is_not_inside_the_repository()
    {
        // C:\repository starts with C:\repo. A naive prefix test accepts it.
        Assert.Equal(RepositoryChangeRelevance.Ignored, Classify(@"C:\repository\src\Program.cs"));
    }

    [Theory]
    [InlineData(@"C:\REPO\src\Program.cs")]
    [InlineData(@"c:\repo\SRC\Program.cs")]
    public void Comparison_is_case_insensitive(string path)
    {
        // Windows paths are, and the watcher reports whatever casing the OS hands it - which
        // is not always the casing the repository was opened with.
        Assert.Equal(RepositoryChangeRelevance.WorkingTree, Classify(path));
    }

    [Fact]
    public void Forward_and_backslashes_are_equivalent()
    {
        Assert.Equal(RepositoryChangeRelevance.WorkingTree,
            RepositoryChangeFilter.Classify("C:/repo", @"C:\repo\src\Program.cs"));
    }

    [Fact]
    public void A_trailing_separator_on_the_root_is_tolerated()
    {
        // Callers pass whatever the repository locator produced, and that has had a trailing
        // slash before now.
        Assert.Equal(RepositoryChangeRelevance.WorkingTree,
            RepositoryChangeFilter.Classify(@"C:\repo\", @"C:\repo\src\Program.cs"));
    }

    [Fact]
    public void Relative_paths_use_forward_slashes_to_match_git()
    {
        // git status reports "src/Program.cs"; comparing against a backslash form silently
        // never matches, which would break the "reload the open diff" check.
        Assert.Equal("src/Program.cs",
            RepositoryChangeFilter.ToRelative(Root, @"C:\repo\src\Program.cs"));
    }

    [Fact]
    public void The_root_itself_relativizes_to_empty()
    {
        Assert.Equal(string.Empty, RepositoryChangeFilter.ToRelative(Root, Root));
    }

    [Fact]
    public void The_root_itself_forces_a_refresh_rather_than_being_ignored()
    {
        // The overflow path enqueues the root: events were lost and there is no way to learn
        // which. Classifying it as Ignored would swallow the one signal that says
        // "everything you think you know is unreliable".
        Assert.Equal(RepositoryChangeRelevance.WorkingTree, Classify(Root));
    }
}
