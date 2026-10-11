using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Iec.Ladder;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A ladder block's Rendered pane is its networks drawn, compared with the committed copy, not
/// highlighted XML.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <c>SelectedNode</c>, the door a click enters by. Like the member table and
/// Markdown arms, the ladder probe in <c>BuildRenderedContent</c> is a pure addition in front of
/// <c>SourceRender</c>. Deleting it would leave everything else green, so this class is what goes
/// red.
/// </para>
/// <para>
/// Does not reference a vendor parser. The shell asks the registry; the pack that understands
/// the bytes answers. The block XML here is the hand-written specimen shape (see
/// <c>TiaPortalLadderTests</c>), trimmed to one rung.
/// </para>
/// </remarks>
public sealed class LadderPaneFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    /// <summary>One LAD network: a contact, a closed contact, a coil.</summary>
    private static string Rung(string a, string b, string coil) => $"""
                  <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
                    <AttributeList>
                      <NetworkSource>
                        <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
                          <Parts>
                            <Access Scope="GlobalVariable" UId="21"><Symbol><Component Name="{a}" /></Symbol></Access>
                            <Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="{b}" /></Symbol></Access>
                            <Access Scope="GlobalVariable" UId="23"><Symbol><Component Name="{coil}" /></Symbol></Access>
                            <Part Name="Contact" UId="24" />
                            <Part Name="Contact" UId="25"><Negated Name="operand" /></Part>
                            <Part Name="Coil" UId="26" />
                          </Parts>
                          <Wires>
                            <Wire UId="31"><Powerrail /><NameCon UId="24" Name="in" /></Wire>
                            <Wire UId="32"><IdentCon UId="21" /><NameCon UId="24" Name="operand" /></Wire>
                            <Wire UId="33"><NameCon UId="24" Name="out" /><NameCon UId="25" Name="in" /></Wire>
                            <Wire UId="34"><IdentCon UId="22" /><NameCon UId="25" Name="operand" /></Wire>
                            <Wire UId="35"><NameCon UId="25" Name="out" /><NameCon UId="26" Name="in" /></Wire>
                            <Wire UId="36"><IdentCon UId="23" /><NameCon UId="26" Name="operand" /></Wire>
                          </Wires>
                        </FlgNet>
                      </NetworkSource>
                      <ProgrammingLanguage>LAD</ProgrammingLanguage>
                    </AttributeList>
                  </SW.Blocks.CompileUnit>
        """;

    private static string Organisation(string rung) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <SW.Blocks.OB ID="0">
            <AttributeList>
              <Name>Main</Name>
              <ProgrammingLanguage>LAD</ProgrammingLanguage>
            </AttributeList>
            <ObjectList>
        {rung}
            </ObjectList>
          </SW.Blocks.OB>
        </Document>
        """;

    private static string FunctionBlock(string rung) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <SW.Blocks.FB ID="0">
            <AttributeList>
              <Interface>
                <Sections>
                  <Section Name="Input">
                    <Member Name="Enable" Datatype="Bool" />
                  </Section>
                </Sections>
              </Interface>
              <Name>Conveyor</Name>
              <ProgrammingLanguage>LAD</ProgrammingLanguage>
            </AttributeList>
            <ObjectList>
        {rung}
            </ObjectList>
          </SW.Blocks.FB>
        </Document>
        """;

    private static string BlockPath(TempRepo repo, string name) =>
        Path.Combine(repo.Path, "plc", "Station", "Blocks", name + ".xml");

    private static void GivenAPortalProjectWithLadder(TempRepo repo)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);
        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            { "id": "b1", "projectPath": "plc/Station.ap21", "exportPath": "plc/Station",
              "vendor": "TiaPortal", "enabled": true }
          ]
        }
        """);

        Directory.CreateDirectory(Path.Combine(repo.Path, "plc", "Station", "Blocks"));
        File.WriteAllText(Path.Combine(repo.Path, "plc", "Station.ap21"), "pointer");
        File.WriteAllText(Path.Combine(repo.Path, "plc", "Station", "_manifest.json"), """
        {
          "schema": 8,
          "vendor": "TiaPortal",
          "bindingId": "b1",
          "project": "Station.ap21",
          "objectCount": 2,
          "objects": [
            { "guid": "ob1", "name": "Main", "type": "OB", "form": "NATIVE",
              "path": "Blocks/Main.xml", "hash": "sha256:0", "bytes": 1 },
            { "guid": "fb1", "name": "Conveyor", "type": "FB", "form": "NATIVE",
              "path": "Blocks/Conveyor.xml", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(BlockPath(repo, "Main"), Organisation(Rung("Start", "Stop", "Motor")));
        File.WriteAllText(BlockPath(repo, "Conveyor"), FunctionBlock(Rung("Run", "Jam", "Belt")));
    }

    private static void CommitEverything(TempRepo repo)
    {
        repo.Git("add -A");
        repo.Git("commit -q -m blocks");
    }

    private static async Task<TreeRow> Row(ShellViewModel shell, string name)
    {
        TreeRow? found = null;
        await Eventually.Until(
            () => (found = Find(shell.Sync.Tree, name)) is not null,
            $"a row named '{name}' appeared in the tree");
        return found!;
    }

    private static TreeRow? Find(IEnumerable<TreeRow> rows, string name)
    {
        foreach (var row in rows)
        {
            if (row.Name == name)
            {
                return row;
            }

            if (Find(row.Children, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public async Task An_unchanged_ladder_block_is_drawn_at_once_with_nothing_marked()
    {
        using var repo = new TempRepo();
        GivenAPortalProjectWithLadder(repo);
        CommitEverything(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Main");

        var ladder = Assert.IsType<LadderRender>(shell.RenderedContent);
        Assert.False(ladder.Pending);
        Assert.Equal("Main", ladder.Comparison.BlockName);
        Assert.False(ladder.Comparison.HasChanges);
        Assert.True(shell.ShowRenderedPane);
    }

    [Fact]
    public async Task A_changed_ladder_block_is_compared_with_its_committed_copy()
    {
        using var repo = new TempRepo();
        GivenAPortalProjectWithLadder(repo);
        CommitEverything(repo);
        File.WriteAllText(BlockPath(repo, "Main"), Organisation(Rung("Start", "Stop_Ok", "Motor")));

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Main");

        await Eventually.Until(
            () => shell.RenderedContent is LadderRender { Pending: false },
            "the committed copy was read and compared");

        var ladder = Assert.IsType<LadderRender>(shell.RenderedContent);
        var row = Assert.Single(ladder.Comparison.Rows);
        Assert.Equal(LadderChange.Changed, row.Change);
        Assert.Equal(LdMark.Modified, row.New!.Marks["25"]);
    }

    [Fact]
    public async Task A_ladder_block_never_committed_says_there_is_nothing_to_compare_with()
    {
        using var repo = new TempRepo();
        GivenAPortalProjectWithLadder(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Main");

        await Eventually.Until(
            () => shell.RenderedContent is LadderRender { Pending: false },
            "the missing committed copy was answered");

        var ladder = Assert.IsType<LadderRender>(shell.RenderedContent);
        Assert.False(ladder.Comparison.HasOldSide);
    }

    [Fact]
    public async Task A_ladder_function_block_draws_its_networks_under_its_interface_table()
    {
        using var repo = new TempRepo();
        GivenAPortalProjectWithLadder(repo);
        CommitEverything(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Conveyor");

        var render = Assert.IsType<InterfaceRender>(shell.RenderedContent);
        Assert.Equal("Conveyor", render.Table.Name);
        Assert.Null(render.Source);
        Assert.NotNull(render.Ladder);
        Assert.Same(render.Ladder, render.Code);
    }

    private static LadderRender Render(string block, string title, bool pending = false)
    {
        var network = new LadderNetwork
        {
            Number = 1,
            Title = title,
            Nodes = [new LdLeftRail("r"), new LdCoil("c", "\"Out\"", LdCoilKind.Normal)],
            Nets = [new LdNet([new LdPinRef("r", LdPins.Out), new LdPinRef("c", LdPins.In)])],
        };
        var body = new LadderBody(block, [network]);
        return new LadderRender
        {
            Path = $"plc/{block}.xml",
            Comparison = LadderDiff.Compare(body, body),
            Style = LadderStyle.Default,
            Pending = pending,
        };
    }

    [Fact]
    public void The_page_is_built_escaped_and_walled_without_starting_a_browser()
    {
        StaHarness.Run(() =>
        {
            var view = new Versio.App.Views.LadderPageView
            {
                Visibility = System.Windows.Visibility.Collapsed,
                Ladder = Render("Main", "<script>bad()</script>"),
            };

            var page = view.PageHtml;
            Assert.Contains("Content-Security-Policy", page, StringComparison.Ordinal);
            Assert.Contains("script-src https://ladder.versio;", page, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>bad", page, StringComparison.Ordinal);
            Assert.Contains("data-block=\"Main\"", page, StringComparison.Ordinal);
            Assert.Null(view.Child);
        });
    }

    [Fact]
    public void The_side_by_side_box_switches_the_ladder_between_merged_and_two_columns()
    {
        // The pane's "Side by side" box did nothing to a ladder (the owner, 2026-10-05). Merged
        // is the default; ticking the box redraws the same comparison in two columns.
        StaHarness.Run(() =>
        {
            var old = new LadderBody("Main", [Render("Main", "a").Comparison.Rows[0].New!.Network]);
            var edited = new LadderBody("Main", [old.Networks[0] with { Title = "b" }]);
            var view = new Versio.App.Views.LadderPageView
            {
                Visibility = System.Windows.Visibility.Collapsed,
                Ladder = new LadderRender
                {
                    Path = "plc/Main.xml", Comparison = LadderDiff.Compare(old, edited), Style = LadderStyle.Default,
                },
            };
            Assert.Contains("data-view=\"merged\"", view.PageHtml, StringComparison.Ordinal);

            view.SideBySide = true;

            Assert.Contains("data-view=\"side-by-side\"", view.PageHtml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_reused_view_drops_the_last_blocks_diagram_while_the_next_is_pending()
    {
        // WPF reuses a template's view when the next selection is also a ladder. Pending must
        // empty the page, or the previous block's networks stay on screen under the new name.
        StaHarness.Run(() =>
        {
            var view = new Versio.App.Views.LadderPageView
            {
                Visibility = System.Windows.Visibility.Collapsed,
                Ladder = Render("First", "first"),
            };
            Assert.Contains("data-block=\"First\"", view.PageHtml, StringComparison.Ordinal);

            view.Ladder = Render("Second", "second", pending: true);

            Assert.DoesNotContain("data-block=\"First\"", view.PageHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("<div class=\"ld-page\"", view.PageHtml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_deleted_ladder_block_draws_its_committed_networks_as_removed()
    {
        // The owner, 2026-10-05: a deleted block opened on its raw XML, all red, the one changed
        // block that never showed as a diagram.
        using var repo = new TempRepo();
        GivenAPortalProjectWithLadder(repo);
        CommitEverything(repo);
        File.Delete(BlockPath(repo, "Main"));

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Main");

        await Eventually.Until(
            () => shell.RenderedContent is LadderRender,
            "the committed copy was drawn");

        var ladder = Assert.IsType<LadderRender>(shell.RenderedContent);
        Assert.Equal(LadderChange.Removed, Assert.Single(ladder.Comparison.Rows).Change);
        Assert.True(shell.ShowRenderedPane);
    }

    [Fact]
    public async Task A_function_block_changed_only_in_its_networks_marks_them_and_puts_no_note_on_the_table()
    {
        // The networks under the table show the change. The table, with no member changed, has
        // no note: the owner dropped it on 2026-10-11 ("no need for that note at all").
        using var repo = new TempRepo();
        GivenAPortalProjectWithLadder(repo);
        CommitEverything(repo);
        File.WriteAllText(BlockPath(repo, "Conveyor"), FunctionBlock(Rung("Run", "Jam_Free", "Belt")));

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Conveyor");

        await Eventually.Until(
            () => shell.RenderedContent is InterfaceRender { Ladder.Pending: false },
            "the networks were compared");

        var render = Assert.IsType<InterfaceRender>(shell.RenderedContent);
        Assert.False(render.Table.HasChangeSummary);
        Assert.Equal(1, render.Ladder!.Comparison.ChangedCount);
    }

    [Fact]
    public async Task A_late_comparison_never_lands_on_another_block()
    {
        // Selection moves on while git reads the first block's committed copy: the second
        // block must keep its own render, never the first one's comparison.
        using var repo = new TempRepo();
        GivenAPortalProjectWithLadder(repo);
        CommitEverything(repo);
        File.WriteAllText(BlockPath(repo, "Main"), Organisation(Rung("Start", "Stop_Ok", "Motor")));

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Main");
        shell.Sync.SelectedNode = await Row(shell, "Conveyor");

        await Task.Delay(500);
        var render = Assert.IsType<InterfaceRender>(shell.RenderedContent);
        Assert.Equal("Conveyor", render.Ladder!.Comparison.BlockName);
    }
}
