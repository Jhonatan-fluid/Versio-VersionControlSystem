using Versio.App.ViewModels;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A git client that answers from a script and counts what it was asked.
/// </summary>
/// <remarks>
/// The point of the history panel is what it does <i>not</i> do — read a log nobody asked for,
/// re-read a commit's files on every expand, fetch a patch before a file is clicked. None of
/// that is observable from the outside: the panel looks identical either way, and the only
/// difference is a process spawn and a wait. Counting the calls is the only way to hold that
/// behaviour still, which is why this fake exists at all.
/// </remarks>
internal sealed class CountingGitClient : IGitClient
{
    public List<(int Count, int Skip)> LogCalls { get; } = [];

    public List<string> FileCalls { get; } = [];

    public List<(string Commit, string Path)> DiffCalls { get; } = [];

    /// <summary>How many commits the fake history holds.</summary>
    public int Available { get; set; } = 120;

    public Task<IReadOnlyList<CommitEntry>> RecentCommitsAsync(
        int count, int skip, CancellationToken ct)
    {
        LogCalls.Add((count, skip));

        IReadOnlyList<CommitEntry> page =
        [
            .. Enumerable.Range(skip, Math.Max(0, Math.Min(count, Available - skip)))
                .Select(i => new CommitEntry
                {
                    Id = $"{i:x40}",
                    ShortId = $"{i:x7}",
                    Subject = $"commit {i}",
                    Author = "Versio Test",
                    When = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero),
                    Age = "moments ago",
                }),
        ];

        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<CommitFile>> CommitFilesAsync(string commit, CancellationToken ct)
    {
        FileCalls.Add(commit);

        IReadOnlyList<CommitFile> files =
        [
            new() { Path = "a.st", Kind = ChangeKind.Modified },
            new() { Path = "b.st", Kind = ChangeKind.Added },
        ];

        return Task.FromResult(files);
    }

    public Task<string> CommitFileDiffAsync(
        string commit, string path, CancellationToken ct, int contextLines = 3)
    {
        DiffCalls.Add((commit, path));
        return Task.FromResult($"diff --git a/{path} b/{path}");
    }

    // --- everything the history panel never touches -------------------------------

    public Task<bool> IsRepositoryAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<string> VersionAsync(CancellationToken ct) => Task.FromResult("git version 2.0");

    public Task<GitStatus> StatusAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<BranchInfo>> BranchesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BranchInfo>>([]);

    public Task<GitResult> StageAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> CommitAsync(string message, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> CreateBranchAsync(string name, bool checkout, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> CheckoutAsync(string branch, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> VerifyRemoteAsync(string url, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<string> RemoteUrlAsync(string remote, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<string> GitDirectoryAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> FetchAsync(IProgress<GitProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> PullAsync(IProgress<GitProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> PushAsync(IProgress<GitProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<string> DiffAsync(string path, bool staged, CancellationToken ct,
                                  int contextLines = 3) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> DetectRenamesAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<string> HeadCommitAsync(CancellationToken ct) => Task.FromResult(string.Empty);

    public Task<string> CommitPatchAsync(string commit, CancellationToken ct,
                                         int contextLines = 3) =>
        Task.FromResult($"diff --git for {commit}");

    public Task<IReadOnlyList<StashEntry>> StashListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StashEntry>>([]);

    public Task<GitResult> StashPushAsync(string? message, bool includeUntracked,
                                          CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> StashApplyAsync(string stashRef, bool drop, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> StashDropAsync(string stashRef, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GitResult> ApplyToIndexAsync(string patch, bool reverse, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<bool> IsLfsAvailableAsync(CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// What the history panel reads, and — mostly — when it declines to.
/// </summary>
public class HistoryViewModelTests
{
    private static HistoryViewModel Attached(CountingGitClient git)
    {
        // A logger with no sinks writes nowhere, which is all this needs it to do.
        var history = new HistoryViewModel(new VersioLogger(new SystemClock()));
        history.Attach(git);
        return history;
    }

    /// <summary>
    /// Opening a repository must not walk its log.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the panel was split out of the git one. The list it replaced
    /// was reloaded on every refresh that touched git's metadata, whether or not anybody had
    /// ever looked at the history — on a repository with a long log, a walk nobody asked for
    /// on the path that opens the window.
    /// </remarks>
    [Fact]
    public void Attaching_a_repository_reads_no_history_at_all()
    {
        var git = new CountingGitClient();
        _ = Attached(git);

        Assert.Empty(git.LogCalls);
    }

    [Fact]
    public void The_first_page_is_read_when_the_panel_is_first_shown()
    {
        var git = new CountingGitClient();
        var history = Attached(git);

        history.IsVisible = true;

        var call = Assert.Single(git.LogCalls);
        Assert.Equal(HistoryViewModel.PageSize, call.Count);
        Assert.Equal(0, call.Skip);
        Assert.Equal(HistoryViewModel.PageSize, history.Commits.Count);
    }

    /// <summary>Leaving the panel and coming back must not re-read a log that has not moved.</summary>
    [Fact]
    public void Showing_the_panel_again_reads_nothing_new()
    {
        var git = new CountingGitClient();
        var history = Attached(git);

        history.IsVisible = true;
        history.IsVisible = false;
        history.IsVisible = true;

        Assert.Single(git.LogCalls);
    }

    /// <summary>
    /// A refresh while the panel is hidden marks it, and reads nothing.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Reading would be the eager cost this design exists to remove; not
    /// marking would leave a commit missing from the history the next time it was opened,
    /// which is worse than the cost.
    /// </remarks>
    [Fact]
    public void A_refresh_behind_a_hidden_panel_defers_the_read_until_it_is_shown()
    {
        var git = new CountingGitClient();
        var history = Attached(git);

        history.Invalidate();
        Assert.Empty(git.LogCalls);

        history.IsVisible = true;
        Assert.Single(git.LogCalls);
    }

    /// <summary>A commit made while the history is in front has to appear at once.</summary>
    [Fact]
    public void A_refresh_in_front_of_an_open_panel_reloads_it_immediately()
    {
        var git = new CountingGitClient();
        var history = Attached(git);

        history.IsVisible = true;
        history.Invalidate();

        Assert.Equal(2, git.LogCalls.Count);
        Assert.All(git.LogCalls, c => Assert.Equal(0, c.Skip));

        // Reloaded, not appended. The same commits arriving twice would be the visible
        // symptom of a page that was never cleared.
        Assert.Equal(HistoryViewModel.PageSize, history.Commits.Count);
    }

    [Fact]
    public void Loading_more_asks_for_the_page_after_the_ones_already_held()
    {
        var git = new CountingGitClient();
        var history = Attached(git);

        history.IsVisible = true;
        history.LoadMoreCommand.Execute(null);

        Assert.Equal(2, git.LogCalls.Count);
        Assert.Equal(HistoryViewModel.PageSize, git.LogCalls[1].Skip);
        Assert.Equal(HistoryViewModel.PageSize * 2, history.Commits.Count);
    }

    /// <summary>The end of the branch is how the panel knows to stop offering another page.</summary>
    [Fact]
    public void A_short_page_means_there_is_nothing_further_back()
    {
        var git = new CountingGitClient { Available = 10 };
        var history = Attached(git);

        history.IsVisible = true;

        Assert.False(history.HasMore);
        Assert.False(history.LoadMoreCommand.CanExecute(null));
        Assert.Equal(10, history.Commits.Count);
    }

    /// <summary>A collapsed commit costs nothing beyond the row itself.</summary>
    [Fact]
    public void A_commits_files_are_not_read_until_it_is_expanded()
    {
        var git = new CountingGitClient();
        var history = Attached(git);
        history.IsVisible = true;

        Assert.Empty(git.FileCalls);

        history.Commits[0].IsExpanded = true;

        Assert.Equal(history.Commits[0].Id, Assert.Single(git.FileCalls));
        Assert.Equal(["a.st", "b.st"], history.Commits[0].Children.Select(c => c.Path));
    }

    /// <summary>
    /// Opening the same commit twice asks git once.
    /// </summary>
    /// <remarks>
    /// Safe precisely because a commit is immutable: its hash is its content, so the answer
    /// cannot have changed between the two expands. The same caching would be wrong for the
    /// working tree, which is why it lives here and not in the git panel.
    /// </remarks>
    [Fact]
    public void Collapsing_and_reopening_a_commit_reads_nothing_further()
    {
        var git = new CountingGitClient();
        var history = Attached(git);
        history.IsVisible = true;

        var commit = history.Commits[0];
        commit.IsExpanded = true;
        commit.IsExpanded = false;
        commit.IsExpanded = true;

        Assert.Single(git.FileCalls);
    }

    /// <summary>Expanding a commit must not fetch the patches of the files it lists.</summary>
    [Fact]
    public void Expanding_a_commit_reads_no_diffs()
    {
        var git = new CountingGitClient();
        var history = Attached(git);
        history.IsVisible = true;

        history.Commits[0].IsExpanded = true;

        Assert.Empty(git.DiffCalls);
    }

    /// <summary>
    /// A file row raises the selection the shell renders; the placeholder rows do not.
    /// </summary>
    /// <remarks>
    /// The "…" that makes the expander appear, and the note explaining an empty merge, are
    /// rows in the same list as the files. Neither names a file, so acting on one would ask
    /// git for the diff of a path that does not exist.
    /// </remarks>
    [Fact]
    public void Picking_a_file_raises_it_and_picking_a_placeholder_does_not()
    {
        var git = new CountingGitClient();
        var history = Attached(git);
        history.IsVisible = true;

        var picked = new List<string>();
        history.FileSelected += row => picked.Add(row.Path);

        var commit = history.Commits[0];

        // The seeded placeholder, before anything has been loaded.
        history.Select(commit.Children[0]);
        Assert.Empty(picked);

        commit.IsExpanded = true;
        history.Select(commit.Children[0]);

        Assert.Equal(["a.st"], picked);
    }

    [Fact]
    public void Picking_a_commit_raises_the_commit_itself()
    {
        var git = new CountingGitClient();
        var history = Attached(git);
        history.IsVisible = true;

        CommitRow? picked = null;
        history.CommitSelected += row => picked = row;

        history.Select(history.Commits[3]);

        Assert.Same(history.Commits[3], picked);
    }

    /// <summary>Switching repositories must not leave the previous one's commits on screen.</summary>
    [Fact]
    public void Attaching_a_different_repository_clears_what_was_loaded()
    {
        var first = new CountingGitClient();
        var history = Attached(first);
        history.IsVisible = true;
        Assert.NotEmpty(history.Commits);

        var second = new CountingGitClient { Available = 3 };
        history.Attach(second);

        // Attached while already in front, so the new repository's first page is read at
        // once - the panel is visible, and leaving it empty would read as a repository with
        // no history.
        Assert.Equal(3, history.Commits.Count);
        Assert.Single(second.LogCalls);
    }
}
