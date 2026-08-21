using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The stash section that exists only while there is something in it — the design the
/// withdrawn six-permanent-controls version owed.
/// </summary>
/// <remarks>
/// Against real stashes made the way a user makes the ones this section is for: in a
/// terminal. The plumbing (<c>StashPushAsync</c> and friends) has been kept and tested since
/// the withdrawal; what these prove is the surface — the section appears and disappears with
/// the list, apply keeps the stash, and drop re-resolves the positional ref by SHA rather
/// than trusting one held across a refresh.
/// </remarks>
public sealed class StashSectionFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private static void GivenAStash(TempRepo repo, string message = "my stash")
    {
        repo.Touch("seed.txt");
        repo.Git($"stash push -m \"{message}\"");
    }

    [Fact]
    public async Task The_section_exists_exactly_while_the_list_has_rows()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.False(shell.HasStashes);

        GivenAStash(repo);
        await shell.RefreshAsync();

        Assert.True(shell.HasStashes);
        Assert.Equal("my stash", Assert.Single(shell.Stashes).Title);
    }

    [Fact]
    public async Task Apply_restores_the_changes_and_keeps_the_stash()
    {
        using var repo = new TempRepo();
        GivenAStash(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var row = Assert.Single(shell.Stashes);
        await Eventually.Press(row.ApplyCommand, "apply the stash");

        // The working tree got the change back — and the stash is still there, because
        // apply-then-drop is two decisions and pop would have made them one.
        Assert.Contains(shell.Pending, r => r.Path == "seed.txt");
        Assert.True(shell.HasStashes);
    }

    [Fact]
    public async Task Drop_asks_first_and_names_the_stash()
    {
        using var repo = new TempRepo();
        GivenAStash(repo, "afternoon experiment");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        string? asked = null;
        shell.Asked().Destructive = p =>
        {
            asked = p.Body;
            return true;
        };

        var row = Assert.Single(shell.Stashes);
        await Eventually.Press(row.DropCommand, "drop the stash");

        Assert.Contains("afternoon experiment", asked);
        Assert.False(shell.HasStashes);
    }

    [Fact]
    public async Task Declining_the_drop_keeps_the_stash()
    {
        using var repo = new TempRepo();
        GivenAStash(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Asked().Destructive = _ => false;

        var row = Assert.Single(shell.Stashes);
        await Eventually.Press(row.DropCommand, "decline the drop");

        Assert.True(shell.HasStashes);
    }

    [Fact]
    public async Task A_stash_dropped_in_a_terminal_since_the_list_was_drawn_deletes_nothing()
    {
        // The positional-ref trap, driven end to end: the row's stash is gone and another
        // sits where its ref used to point. A drop that trusted the stale ref would delete
        // the survivor — the exact failure StashEntry's own remark documents.
        using var repo = new TempRepo();
        GivenAStash(repo, "older");
        GivenAStash(repo, "newer");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Asked().Destructive = _ => true;

        var newer = shell.Stashes.Single(s => s.Title == "newer");
        repo.Git("stash drop stash@{0}");

        await Eventually.Press(newer.DropCommand, "drop the vanished stash");

        // "older" must survive: the row's target was already gone, and nothing else may
        // stand in for it.
        Assert.Equal("older", Assert.Single(shell.Stashes).Title);
    }
}
