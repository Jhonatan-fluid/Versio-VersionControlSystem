using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Plc;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A vendor's fix, offered where the refusal is seen and applied from there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for 2026-10-29</b>, after the owner met TIA Portal's Openness refusal as a
/// failure card an hour into a new machine and fixed it from a command in chat: detect it
/// before Portal is launched, and put the fix on the project card, on the failure pane and in
/// Settings. This drives the neutral half through the shell with a toolchain whose probe
/// refuses with a remedy and whose fix makes it available: the card carries the remedy and
/// the vendor's own sentence in its Toolchain row; the failed export's pane carries the same
/// remedy; pressing it applies the fix once, says so in a notification, probes again, and
/// every offer disappears. The vendor's words and the Windows group are the vendor's tests.
/// </para>
/// </remarks>
public sealed class ToolchainRemedyFlowTests
{
    private static TempRepo RepositoryWithOneProject()
    {
        var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Path, "plc"));
        File.WriteAllBytes(Path.Combine(repo.Path, "plc", "Machine.project"), [0x01, 0x02, 0x03]);
        return repo;
    }

    private static async Task<TreeRow> Row(ShellViewModel shell, string name)
    {
        TreeRow? found = null;
        await Eventually.Until(
            () => (found = shell.Sync.Tree.FirstOrDefault(r => r.Name == name)) is not null,
            $"a row named '{name}' appeared in the tree");
        return found!;
    }

    [Fact]
    public async Task The_card_the_failure_pane_and_the_fix_follow_the_vendors_offer()
    {
        using var repo = RepositoryWithOneProject();
        var toolchain = new RemedyToolchain();
        using var shell = TestShell.Build(out var sink, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);

        string State() => string.Join("\n", sink.Snapshot().Select(e => $"{e.Level} {e.Category} {e.Message}"));

        // Adoption. No catch-up export follows: a refused toolchain is not asked to export on
        // open (that is why the owner's morning started with "not exported", not a failure).
        var row = await Row(shell, "Machine.project");
        await Eventually.Until(() => shell.Sync.Projects.Any(p => p.Tracked), "the project was adopted", State);

        // A commit drives the gate export, which the refused toolchain fails: the state the
        // owner was in once Portal had been asked and had said no.
        shell.CommitMessage = "first";
        await Eventually.Press(shell.CommitCommand, "commit", State);
        await Eventually.Until(
            () => shell.Sync.Projects.Any(p => p.Tracked && p.State == "failed"),
            "the gate export failed on the refused toolchain", State);

        // The card: the vendor's own sentence where "installed" used to be, and the remedy.
        shell.Sync.SelectedNode = row;
        var card = Assert.IsType<ProjectSummaryRender>(shell.RenderedContent);
        Assert.Equal("Fake IDE is installed, but this account is not in the group 'Fake'.",
            card.Facts.Single(f => f.Label == "Toolchain").Value);
        Assert.True(card.HasRemedy);
        Assert.Equal(RemedyToolchain.Remedy.Action, card.Remedy!.Action);
        Assert.Equal("plc/Machine.project", card.ProjectPath);

        // The failure pane: the same remedy under the failure, and the command is live.
        Assert.True(shell.Sync.HasFailure);
        Assert.True(shell.Sync.HasFailureRemedy);
        Assert.Same(RemedyToolchain.Remedy, shell.Sync.FailureRemedy);
        Assert.True(shell.FixToolchainCommand.CanExecute("plc/Machine.project"));
        Assert.False(shell.FixToolchainCommand.CanExecute(null));

        // Press it — through the command, the way the two buttons on the main window do.
        var probesBefore = toolchain.Probes;
        shell.FixToolchainCommand.Execute("plc/Machine.project");
        await Eventually.Until(() => toolchain.Applied == 1 && !shell.Sync.RemedyInFlight, "the fix ran once", State);
        var message = shell.Notifications.Visible.Single(n => n.Headline == "Done.").Detail;

        Assert.Equal(1, toolchain.Applied);
        Assert.StartsWith("added the account to 'Fake'", message);
        Assert.Contains(shell.Notifications.Visible, n =>
            n.Headline == "Done." && n.Detail.StartsWith("added the account to 'Fake'", StringComparison.Ordinal));
        Assert.True(toolchain.Probes > probesBefore, "the toolchain was not probed again after the fix");

        // Probed again: no offer anywhere, and the card says installed.
        Assert.Null(shell.Sync.RemedyFor("plc/Machine.project"));
        Assert.False(shell.Sync.HasFailureRemedy);
        Assert.False(shell.FixToolchainCommand.CanExecute("plc/Machine.project"));
        var after = Assert.IsType<ProjectSummaryRender>(shell.RenderedContent);
        Assert.False(after.HasRemedy);
        Assert.StartsWith("installed", after.Facts.Single(f => f.Label == "Toolchain").Value);

        // In the log, both ends.
        Assert.Contains(sink.Snapshot(), e => e.Message.Contains("applying the offered fix", StringComparison.Ordinal));
        Assert.Contains(sink.Snapshot(), e => e.Message.Contains("added the account to 'Fake'", StringComparison.Ordinal));

        // And Try again runs on the rebuilt runner, not the refusing one built at open
        // (review, 2026-10-29): the export goes through and the failure is gone.
        Assert.Contains(sink.Snapshot(), e => e.Message.Contains("rebuilding the sync engine", StringComparison.Ordinal));
        shell.Sync.RefreshFailure("plc/Machine.project");
        Assert.True(shell.Sync.HasFailure);
        await shell.Sync.RetryAsync();
        Assert.Single(toolchain.Jobs, j => j.Operation == PlcOperation.Export);
        Assert.False(shell.Sync.HasFailure);
    }

    [Fact]
    public async Task A_fix_that_does_not_apply_says_so_and_leaves_the_offer_up()
    {
        using var repo = RepositoryWithOneProject();
        var toolchain = new RemedyToolchain { Refuse = "the administrator prompt was declined, so nothing was changed." };
        using var shell = TestShell.Build(out var sink, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);
        await Row(shell, "Machine.project");

        var message = await shell.FixToolchainAsync(PlcVendor.Codesys, CancellationToken.None);

        Assert.Equal(1, toolchain.Applied);
        Assert.Equal("the administrator prompt was declined, so nothing was changed.", message);
        Assert.Contains(shell.Notifications.Visible, n =>
            n.Headline == "Nothing was changed." && n.Detail.Contains("declined", StringComparison.Ordinal));
        Assert.NotNull(shell.Sync.RemedyFor("plc/Machine.project"));
        Assert.Contains(sink.Snapshot(), e =>
            e.Level == Versio.Core.Logging.LogLevel.Warning && e.Message.Contains("did not apply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fix_that_has_not_finished_is_said_as_uncertain_not_as_nothing_changed()
    {
        // The helper's timeout leaves it running, so the account may yet be added; "Nothing
        // was changed" would have been a claim the shell cannot make (review, 2026-10-29).
        using var repo = RepositoryWithOneProject();
        var toolchain = new RemedyToolchain { Unsure = "the elevated helper has not finished after 5 minutes and is still running; check the group before pressing again." };
        using var shell = TestShell.Build(out var sink, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);
        await Row(shell, "Machine.project");

        var message = await shell.FixToolchainAsync(PlcVendor.Codesys, CancellationToken.None);

        Assert.StartsWith("the elevated helper has not finished", message);
        Assert.Contains(shell.Notifications.Visible, n =>
            n.Headline == "Not finished." && n.Detail.Contains("still running", StringComparison.Ordinal));
        Assert.DoesNotContain(shell.Notifications.Visible, n => n.Headline is "Nothing was changed." or "Done.");
        Assert.Contains(sink.Snapshot(), e =>
            e.Level == Versio.Core.Logging.LogLevel.Warning && e.Message.Contains("has not finished", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_fix_acts_on_the_status_it_was_handed_even_with_no_repository_open()
    {
        // Settings probes afresh when it opens, repository or not. Its button acted on the
        // repository's cached probe until review on 2026-10-29 — absent before a repository is
        // opened — and answered "offers no fix right now" to a row that plainly offered one.
        var toolchain = new RemedyToolchain();
        using var shell = TestShell.Build(out _, toolchain: toolchain);
        var status = await toolchain.ProbeAsync(CancellationToken.None);

        var message = await shell.FixToolchainAsync(toolchain, status, CancellationToken.None);

        Assert.Equal(1, toolchain.Applied);
        Assert.StartsWith("added the account to 'Fake'", message);
        Assert.Contains(shell.Notifications.Visible, n => n.Headline == "Done.");
    }

    [Fact]
    public async Task A_vendor_with_nothing_to_offer_is_refused_without_a_notification_of_success()
    {
        using var repo = RepositoryWithOneProject();
        using var shell = TestShell.Build(out _, toolchain: new CapturingToolchain());
        await shell.OpenAsync(repo.Path);

        var message = await shell.FixToolchainAsync(PlcVendor.Codesys, CancellationToken.None);

        Assert.Contains("offers no fix", message);
        Assert.DoesNotContain(shell.Notifications.Visible, n => n.Headline == "Done.");
    }
}
