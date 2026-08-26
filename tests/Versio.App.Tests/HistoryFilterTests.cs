using System.Windows.Data;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// UX-6: the find box over the history, and the two things it must not do.
/// </summary>
/// <remarks>
/// It must not narrow the walk — "Load older commits" keeps paging the unfiltered log, or the
/// box becomes an unbounded <c>git log</c> over the whole branch. And it must not touch the
/// loaded set — a hidden commit keeps its cached file list and its place in the paging
/// arithmetic. Both are the same rule the changes-list filter follows: a veil over the view,
/// never a change to the set.
/// </remarks>
public sealed class HistoryFilterTests
{
    private static HistoryViewModel Shown(CountingGitClient git)
    {
        var history = new HistoryViewModel(new VersioLogger(new SystemClock()));
        history.Attach(git);
        history.IsVisible = true;
        return history;
    }

    /// <summary>What the TreeView would show: the default view, filtered.</summary>
    private static string[] Visible(HistoryViewModel history) =>
        CollectionViewSource.GetDefaultView(history.Commits)
            .Cast<CommitRow>().Select(r => r.Subject).ToArray();

    [Fact]
    public void The_filter_hides_commits_without_dropping_them()
    {
        var git = new CountingGitClient();
        var history = Shown(git);

        history.Filter = "commit 7";

        // Of the fifty loaded subjects, only "commit 7" contains "commit 7".
        Assert.Equal(["commit 7"], Visible(history));
        Assert.Equal(HistoryViewModel.PageSize, history.Commits.Count);

        history.Filter = string.Empty;
        Assert.Equal(HistoryViewModel.PageSize, Visible(history).Length);
    }

    [Fact]
    public void Load_more_keeps_walking_the_unfiltered_history()
    {
        // The trap: paging by what is VISIBLE would re-read the same page forever — or worse,
        // let the filter reach into git and walk the whole branch looking for matches. The
        // skip has to count loaded commits, hidden ones included.
        var git = new CountingGitClient();
        var history = Shown(git);

        history.Filter = "commit 7";
        history.LoadMoreCommand.Execute(null);

        Assert.Equal(2, git.LogCalls.Count);
        Assert.Equal(HistoryViewModel.PageSize, git.LogCalls[1].Skip);
        Assert.Equal(2 * HistoryViewModel.PageSize, history.Commits.Count);

        // And the page that just arrived is matched too: "commit 70" through "commit 79"
        // join "commit 7" without the filter being touched.
        Assert.Equal(11, Visible(history).Length);
    }

    [Fact]
    public void The_filter_matches_the_author_too()
    {
        var git = new CountingGitClient();
        var history = Shown(git);

        history.Filter = "versio test";

        Assert.Equal(HistoryViewModel.PageSize, Visible(history).Length);
    }

    [Fact]
    public void An_empty_result_is_named_as_the_filter_s_doing()
    {
        var git = new CountingGitClient();
        var history = Shown(git);

        Assert.False(history.FilterHidesEverything);

        history.Filter = "zzz";
        Assert.True(history.FilterHidesEverything);

        history.Filter = "commit 1";
        Assert.False(history.FilterHidesEverything);
    }

    [Fact]
    public void The_filter_resets_when_the_repository_changes()
    {
        var git = new CountingGitClient();
        var history = Shown(git);

        history.Filter = "commit 1";
        history.Attach(new CountingGitClient());

        Assert.Equal(string.Empty, history.Filter);
    }
}
