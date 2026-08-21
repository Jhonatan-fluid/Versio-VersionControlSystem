using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Per-file include set: what survives a refresh, what an action applies to, and what the
/// header says about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first test here is the one everything else rests on.</b> Before GP-E,
/// the lists were cleared and refilled on every refresh — and a refresh fires whenever the
/// working tree moves. A checkbox on a row that is replaced a second later is not a feature.
/// </para>
/// </remarks>
public sealed class StagingSelectionFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    [Fact]
    public async Task A_tick_survives_a_refresh()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal(4, shell.Pending.Count);

        var picked = shell.Pending[1];
        picked.IsSelected = true;

        repo.Touch("file3.st");
        await shell.RefreshAsync();

        Assert.True(picked.IsSelected);
        Assert.Same(picked, shell.Pending.Single(r => r.Path == picked.Path));
        Assert.Equal(1, shell.PendingSelectedCount);
    }

    [Fact]
    public async Task A_row_that_goes_away_takes_its_tick_with_it()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        foreach (var row in shell.Pending)
        {
            row.IsSelected = true;
        }

        Assert.Equal(3, shell.PendingSelectedCount);

        repo.Delete("file0.st");
        await shell.RefreshAsync();

        Assert.Equal(2, shell.Pending.Count);
        Assert.Equal(2, shell.PendingSelectedCount);
    }

    [Fact]
    public async Task Discard_honors_the_ticked_set()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        repo.Git("add -A");
        repo.Git("commit -qm files");
        repo.Touch("file0.st");
        repo.Touch("file1.st");
        repo.Touch("file2.st");
        repo.Touch("file3.st");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        Assert.Equal(4, shell.Pending.Count);

        shell.Pending[0].IsSelected = true;
        shell.Pending[2].IsSelected = true;
        var kept = new[] { shell.Pending[1].Path, shell.Pending[3].Path };

        shell.Asked().Destructive = _ => true;
        await Eventually.Press(shell.DiscardCommand, "discard");
        await Eventually.Until(() => shell.Pending.Count == 2, "two files were discarded");

        Assert.Equal(kept.OrderBy(p => p), shell.Pending.Select(r => r.Path).OrderBy(p => p));
    }

    [Fact]
    public async Task The_master_checkbox_ticks_and_clears_the_list()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.False(shell.PendingAllSelected);

        shell.PendingAllSelected = true;
        Assert.Equal(4, shell.PendingSelectedCount);
        Assert.True(shell.PendingAllSelected);

        shell.PendingAllSelected = false;
        Assert.Equal(0, shell.PendingSelectedCount);
    }

    [Fact]
    public async Task The_master_checkbox_goes_indeterminate_for_a_partial_selection()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.Pending[0].IsSelected = true;

        Assert.Null(shell.PendingAllSelected);
    }

    [Fact]
    public async Task The_labels_say_what_will_happen()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal("Discard all", shell.DiscardLabel);
        Assert.Equal("CHANGES · 4", shell.PendingHeader);
        Assert.Equal(string.Empty, shell.PendingSelectionCaption);

        shell.Pending[0].IsSelected = true;
        shell.Pending[1].IsSelected = true;

        Assert.Equal("Discard 2", shell.DiscardLabel);
        Assert.Equal("2 of 4 selected", shell.PendingSelectionCaption);
        Assert.Equal("CHANGES · 2 of 4 selected", shell.PendingHeader);
    }

    [Fact]
    public async Task Ticking_one_row_updates_the_header_without_anything_else_happening()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        var raised = new List<string>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        shell.Pending[0].IsSelected = true;

        Assert.Contains(nameof(shell.PendingSelectedCount), raised);
        Assert.Contains(nameof(shell.DiscardLabel), raised);
    }

    [Fact]
    public async Task The_list_selection_and_the_checkbox_are_different_things()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        shell.SelectedPending = shell.Pending[1];

        Assert.Equal(0, shell.PendingSelectedCount);
        Assert.False(shell.Pending[1].IsSelected);

        shell.Pending[2].IsSelected = true;
        Assert.Same(shell.Pending[1], shell.SelectedPending);
    }
}
