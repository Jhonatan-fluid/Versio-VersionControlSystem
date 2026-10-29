using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A vendor's notice for the next run, on the project card beside "installed".
/// </summary>
/// <remarks>
/// <b>2026-10-29.</b> TIA Portal puts up its Openness access dialog the first time a host
/// build connects, and an unanswered one is a nine-minute silence ending in a timeout that
/// names neither. The vendor now reads the list and says so on an available status; this is
/// the neutral half — the card carries the sentence before anybody presses anything, and a
/// toolchain with nothing to say keeps the old fact. The vendor's words and the registry are
/// the vendor's tests.
/// </remarks>
public sealed class ToolchainNoticeTests
{
    [Fact]
    public async Task The_card_carries_the_notice_beside_installed_and_only_when_there_is_one()
    {
        const string notice = "Fake IDE will ask for access the first time this host connects; answer Yes to all.";
        using var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Path, "plc"));
        File.WriteAllBytes(Path.Combine(repo.Path, "plc", "Machine.project"), [0x01, 0x02, 0x03]);

        using (var shell = TestShell.Build(out _, toolchain: new CapturingToolchain { Notice = notice }))
        {
            await shell.OpenAsync(repo.Path);
            await Eventually.Until(
                () => shell.Sync.ToolchainStatusFor("plc/Machine.project") is not null,
                "the toolchain was probed");

            Assert.Equal($"installed. {notice}", shell.Sync.ToolchainFactFor("plc/Machine.project"));

            TreeRow? row = null;
            await Eventually.Until(
                () => (row = shell.Sync.Tree.FirstOrDefault(r => r.Name == "Machine.project")) is not null,
                "the project row appeared");
            shell.Sync.SelectedNode = row;
            var card = Assert.IsType<ProjectSummaryRender>(shell.RenderedContent);
            Assert.Equal($"installed. {notice}", card.Facts.Single(f => f.Label == "Toolchain").Value);
            Assert.False(card.HasRemedy, "a notice is not a fix; nothing is offered to press");
        }

        using (var quiet = TestShell.Build(out _, toolchain: new CapturingToolchain()))
        {
            await quiet.OpenAsync(repo.Path);
            await Eventually.Until(
                () => quiet.Sync.ToolchainStatusFor("plc/Machine.project") is not null,
                "the toolchain was probed");

            Assert.Equal("installed — version under Settings → System information",
                quiet.Sync.ToolchainFactFor("plc/Machine.project"));
        }
    }
}
