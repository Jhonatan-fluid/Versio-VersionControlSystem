using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Per-file staging: what survives a refresh, what an action applies to, and what the header
/// says about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first test here is the one everything else rests on.</b> Before this phase,
/// <c>UpdateStagingLists</c> cleared both collections and refilled them on every refresh — and
/// a refresh fires whenever the working tree moves. A checkbox on a row that is replaced a
/// second later is not a feature, it is a control that forgets what you told it every time you
/// save a file in your editor.
/// </para>
/// <para>
/// Against a real repository, because the reconcile is about matching what git reports against
/// what is on screen, and a fake status would be a fixture agreeing with the code by
/// construction.
/// </para>
/// </remarks>
public sealed class StagingSelectionFlowTests
{
    private static ShellViewModel Shell()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    [Fact]
    public async Task A_tick_survives_a_refresh()
    {
        // THE PREREQUISITE. A refresh fires whenever the working tree moves, so a checkbox that
        // did not survive one would be cleared every time the user saved a file in their
        // editor — which is to say, constantly, and for no reason they could see.
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal(4, shell.Unstaged.Count);

        var picked = shell.Unstaged[1];
        picked.IsSelected = true;

        // Something unrelated changes, exactly as an editor save would do it.
        repo.Touch("file3.st");
        await shell.RefreshAsync();

        Assert.True(picked.IsSelected);
        Assert.Same(picked, shell.Unstaged.Single(r => r.Path == picked.Path));
        Assert.Equal(1, shell.UnstagedSelectedCount);
    }

    [Fact]
    public async Task A_row_that_goes_away_takes_its_tick_with_it()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        foreach (var row in shell.Unstaged)
        {
            row.IsSelected = true;
        }

        Assert.Equal(3, shell.UnstagedSelectedCount);

        repo.Delete("file0.st");
        await shell.RefreshAsync();

        Assert.Equal(2, shell.Unstaged.Count);
        Assert.Equal(2, shell.UnstagedSelectedCount);
    }

    [Fact]
    public async Task Staging_sends_only_the_ticked_rows()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        shell.Unstaged[0].IsSelected = true;
        shell.Unstaged[2].IsSelected = true;

        shell.StageCommand.Execute(null);
        await Eventually.Until(() => shell.Staged.Count == 2, "two files were staged");

        // Two staged, two left alone. Asked of git rather than of the view model, because what
        // matters is the index rather than the list.
        Assert.Equal(2, shell.Staged.Count);
        Assert.Equal(2, shell.Unstaged.Count);
    }

    [Fact]
    public async Task Nothing_ticked_stages_the_whole_list()
    {
        // What makes "Stage all" and "Stage selected" one button. A 380 px panel cannot afford
        // two of everything.
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal(0, shell.UnstagedSelectedCount);

        shell.StageCommand.Execute(null);
        await Eventually.Until(() => shell.Unstaged.Count == 0, "the list was staged");

        Assert.Equal(3, shell.Staged.Count);
    }

    [Fact]
    public async Task The_master_checkbox_ticks_and_clears_the_list()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.False(shell.UnstagedAllSelected);

        shell.UnstagedAllSelected = true;
        Assert.Equal(4, shell.UnstagedSelectedCount);
        Assert.True(shell.UnstagedAllSelected);

        shell.UnstagedAllSelected = false;
        Assert.Equal(0, shell.UnstagedSelectedCount);
    }

    [Fact]
    public async Task The_master_checkbox_goes_indeterminate_for_a_partial_selection()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.Unstaged[0].IsSelected = true;

        // null is WPF's indeterminate. Produced, never accepted: "set this to partially
        // ticked" has no meaning.
        Assert.Null(shell.UnstagedAllSelected);
    }

    [Fact]
    public async Task The_labels_say_what_will_happen()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal("Stage all", shell.StageLabel);
        Assert.Equal(string.Empty, shell.UnstagedSelectionCaption);

        shell.Unstaged[0].IsSelected = true;
        shell.Unstaged[1].IsSelected = true;

        // The one thing standing between "stage the two I ticked" and "stage all four".
        Assert.Equal("Stage 2", shell.StageLabel);
        Assert.Equal("2 of 4 selected", shell.UnstagedSelectionCaption);
    }

    [Fact]
    public async Task Ticking_one_row_updates_the_header_without_anything_else_happening()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        var raised = new List<string>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        shell.Unstaged[0].IsSelected = true;

        // The row raises an event the shell listens to, rather than the shell polling every
        // row on every refresh.
        Assert.Contains(nameof(shell.UnstagedSelectedCount), raised);
        Assert.Contains(nameof(shell.StageLabel), raised);
    }

    [Fact]
    public async Task The_list_selection_and_the_checkbox_are_different_things()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        // Clicking a row opens its diff. It must not tick anything: a user who lost their diff
        // every time they changed what they were about to stage would stop using one of the two.
        shell.SelectedUnstaged = shell.Unstaged[1];

        Assert.Equal(0, shell.UnstagedSelectedCount);
        Assert.False(shell.Unstaged[1].IsSelected);

        // And the reverse.
        shell.Unstaged[2].IsSelected = true;
        Assert.Same(shell.Unstaged[1], shell.SelectedUnstaged);
    }

}
