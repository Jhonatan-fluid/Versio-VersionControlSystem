using Versio.Core.Iec.Ladder;
using Versio.Plc.TiaPortal;
using Versio.Plc.TiaPortal.Ladder;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// SimaticML LAD into the neutral ladder model, from hand-written specimens.
/// </summary>
/// <remarks>
/// <para>
/// <b>The specimens are not Portal exports.</b> No fixture has ladder logic: <c>Main_1</c>'s only
/// network is empty and every other graphical block is FBD. They were written against the FlgNet
/// schema the fixture's FBD blocks use. When a real export exists it replaces them, and these
/// tests say what changed. See <c>docs/vendors/tia/DRIVING.md</c>, "Graphical blocks".
/// </para>
/// <para>
/// <b>Approved drawings.</b> Each specimen's page is compared with
/// <c>Specimens/Ladder/Approved/*.approved.html</c>. A difference writes <c>*.received.html</c>
/// beside it and fails. Approving is copying received over approved after looking at it in a
/// browser, never automatically: the point of the file is that a human saw it once.
/// </para>
/// </remarks>
public sealed class TiaPortalLadderTests
{
    private static readonly string Specimens =
        Path.Combine(TestPaths.Root, "tests", "Versio.Plc.TiaPortal.Pure.Tests", "Specimens", "Ladder");

    private static readonly string CyclicBlocks = Path.Combine(
        TestPaths.Fixtures, "projects", "CleaningStation_V20_V21", "CleaningStation_V20_V21",
        "ET 200SP station_2", "CleaningStation", "Program blocks", "01_CyclicBlocks");

    public static TheoryData<string> AllSpecimens => new(
        Directory.GetFiles(Specimens, "*.xml").Select(Path.GetFileNameWithoutExtension).Order()!);

    private static LadderDocument Parse(string name)
    {
        var document = TiaPortalProjectFormat.Instance.TryParseLadder(
            $"Program blocks/{name}.xml", File.ReadAllText(Path.Combine(Specimens, name + ".xml")));
        Assert.NotNull(document);
        return document;
    }

    private static string Shape(LadderNetwork network) => ShapeOf(LadderStructure.Of(network).Root);

    private static string ShapeOf(LdBlock? block) => block switch
    {
        null => "",
        LdLeaf leaf => leaf.Node.Id,
        LdSeries s => "S(" + string.Join(",", s.Items.Select(ShapeOf)) + ")",
        LdParallel p => "P(" + string.Join("|", p.Branches.Select(ShapeOf)) + ")",
        _ => "?",
    };

    private static T Node<T>(LadderNetwork network, string id) where T : LdNode =>
        Assert.IsType<T>(network.Nodes.Single(n => n.Id == id));

    [Fact]
    public void A_series_rung_reads_as_contact_closed_contact_coil()
    {
        var document = Parse("Series");
        var network = Assert.Single(document.Body.Networks);

        Assert.Equal("Series", document.Body.BlockName);
        Assert.Equal("Motor run", network.Title);
        Assert.Equal("Start, unless stopped.", network.Comment);
        Assert.Equal("S(24,25,26)", Shape(network));
        Assert.Equal(LdContactKind.NormallyOpen, Node<LdContact>(network, "24").Kind);
        Assert.Equal("\"Start\"", Node<LdContact>(network, "24").Operand);

        // The Negated child on the operand pin is what makes a contact normally closed.
        Assert.Equal(LdContactKind.NormallyClosed, Node<LdContact>(network, "25").Kind);
        Assert.Equal("\"Motor\"", Node<LdCoil>(network, "26").Operand);
    }

    [Fact]
    public void Branches_join_by_a_shared_wire_or_through_an_or_junction_that_is_not_drawn()
    {
        var networks = Parse("Parallel").Body.Networks;

        Assert.Equal("S(P(25|26),27,28)", Shape(networks[0]));

        // The O part is a junction: it folds into the net and is no element of its own.
        Assert.Equal("S(P(45|46),P(48|49))", Shape(networks[1]));
        Assert.DoesNotContain(networks[1].Nodes, n => n.Id == "47");
        Assert.Equal(LdCoilKind.Set, Node<LdCoil>(networks[1], "48").Kind);
        Assert.Equal(LdCoilKind.Reset, Node<LdCoil>(networks[1], "49").Kind);
    }

    [Fact]
    public void A_timer_is_a_box_with_its_instance_its_type_and_its_operands()
    {
        var network = Parse("Timer").Body.Networks[0];
        var timer = Node<LdBox>(network, "26");

        Assert.Equal("S(25,26,27)", Shape(network));
        Assert.Equal("TON", timer.TypeName);
        Assert.Equal("\"Delay_DB\"", timer.Instance);
        Assert.Equal("Time", timer.Detail);
        Assert.Equal("T#5s", timer.Pins.Single(p => p.Name == "PT").Operand);
        Assert.Equal("#elapsed", timer.Pins.Single(p => p.Name == "ET").Operand);
        Assert.Equal(LdPinDirection.Output, timer.Pins.Single(p => p.Name == "Q").Direction);
    }

    [Fact]
    public void A_comparator_is_in_line_and_a_call_carries_its_parameters()
    {
        var network = Parse("CallAndCompare").Body.Networks[0];

        var compare = Node<LdBox>(network, "27");
        Assert.True(compare.InlineCompare);
        Assert.Equal(">", compare.TypeName);
        Assert.Equal("Real", compare.Detail);
        Assert.Equal(["#level", "80.0"], compare.Pins.Where(p => p.Operand is not null).Select(p => p.Operand));

        var call = Node<LdBox>(network, "28");
        Assert.Equal("Pump_Start", call.TypeName);
        Assert.Equal("FC", call.Detail);
        Assert.Equal(["en", "Cmd", "Running", "eno"], call.Pins.Select(p => p.Name));
        Assert.Equal("\"Pump_Cmd\"", call.Pins.Single(p => p.Name == "Cmd").Operand);

        var move = Node<LdBox>(network, "29");
        Assert.Equal("MOVE", move.TypeName);
        Assert.Equal("#speed", move.Pins.Single(p => p.Name == "out1").Operand);
        Assert.Equal("S(27,28,29)", Shape(network));
    }

    [Fact]
    public void Edge_contacts_and_coils_keep_their_edge_memory()
    {
        var networks = Parse("Edges").Body.Networks;

        var rising = Node<LdContact>(networks[0], "24");
        Assert.Equal(LdContactKind.PositiveEdge, rising.Kind);
        Assert.Equal("\"Edge_Mem\"", rising.EdgeMemory);

        var falling = Node<LdCoil>(networks[1], "45");
        Assert.Equal(LdCoilKind.NegativeEdge, falling.Kind);
        Assert.Equal("\"Edge_Mem2\"", falling.EdgeMemory);
    }

    [Fact]
    public void Nothing_is_dropped_an_unknown_part_and_an_scl_network_are_kept()
    {
        var networks = Parse("Mixed").Body.Networks;

        Assert.Equal(3, networks.Count);
        Assert.Equal("FancyThing", Node<LdUnsupported>(networks[0], "24").VendorName);
        Assert.Equal("S(23,24,25)", Shape(networks[0]));
        Assert.Equal("SCL", networks[1].UnsupportedLanguage);
        Assert.Empty(networks[2].Nodes);
    }

    [Fact]
    public void Two_exports_of_a_block_compare_by_content_whatever_their_ids()
    {
        var old = Parse("Pair_old").Body;
        var @new = Parse("Pair_new").Body;

        var result = LadderDiff.Compare(old, @new);

        Assert.Equal(
            [LadderChange.Changed, LadderChange.Replaced, LadderChange.Same],
            result.Rows.Select(r => r.Change));
        Assert.Equal(LdMark.Modified, result.Rows[0].New!.Marks["125"]);
    }

    [Fact]
    public void The_fixtures_fbd_blocks_are_not_ladder_and_keep_their_view()
    {
        // Cyclic interrupt is FBD. Same network schema, different language: drawing it as
        // ladder would draw something the IDE does not show.
        var path = Path.Combine(CyclicBlocks, "Cyclic interrupt.xml");
        Assert.Null(TiaPortalProjectFormat.Instance.TryParseLadder("x/Cyclic interrupt.xml", File.ReadAllText(path)));
    }

    [Fact]
    public void The_fixtures_main_is_ladder_with_one_empty_network()
    {
        var path = Path.Combine(CyclicBlocks, "Main_1.xml");

        var document = TiaPortalProjectFormat.Instance.TryParseLadder("x/Main_1.xml", File.ReadAllText(path));

        Assert.NotNull(document);
        Assert.Equal("Main_1", document.Body.BlockName);
        var network = Assert.Single(document.Body.Networks);
        Assert.Empty(network.Nodes);
    }

    [Theory]
    [InlineData("x/Series.scl", "<Document />")]
    [InlineData("x/Series.xml", "not xml")]
    [InlineData("x/Limits.xml", "<Document><SW.Blocks.GlobalDB><AttributeList><ProgrammingLanguage>DB</ProgrammingLanguage></AttributeList></SW.Blocks.GlobalDB></Document>")]
    public void Anything_that_is_not_a_ladder_block_is_null(string path, string content)
    {
        Assert.Null(TiaPortalProjectFormat.Instance.TryParseLadder(path, content));
    }

    [Theory]
    [MemberData(nameof(AllSpecimens))]
    public void Every_specimen_draws_within_the_shared_invariants(string name)
    {
        foreach (var network in Parse(name).Body.Networks)
        {
            var problems = LadderInvariants.Violations(LadderLayout.Lay(network, TiaPortalLadderParser.Style));
            Assert.True(problems.Count == 0, $"{name} network {network.Number}: {string.Join("; ", problems)}");
        }
    }

    [Theory]
    [MemberData(nameof(AllSpecimens))]
    public void Every_specimen_draws_as_approved(string name) => Approve(name, LadderView.Merged, "");

    [Fact]
    public void The_diff_pair_draws_side_by_side_as_approved() =>
        Approve("Pair_new", LadderView.SideBySide, ".side-by-side");

    private static void Approve(string name, LadderView view, string suffix)
    {
        var body = Parse(name).Body;
        var other = name.EndsWith("_new", StringComparison.Ordinal)
            ? Parse(name.Replace("_new", "_old", StringComparison.Ordinal)).Body
            : body;
        var html = LadderHtml.Fragment(LadderDiff.Compare(other, body), TiaPortalLadderParser.Style, view);

        var folder = Path.Combine(Specimens, "Approved");
        var approved = Path.Combine(folder, name + suffix + ".approved.html");
        var received = Path.Combine(folder, name + suffix + ".received.html");
        if (File.Exists(approved) && File.ReadAllText(approved) == html)
        {
            File.Delete(received);
            return;
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(received, html);
        Assert.Fail($"{name}: the drawing differs from {approved}. Look at {received} in a browser; if it is right, copy it over the approved file.");
    }

    [Fact]
    public void A_gallery_of_every_specimen_is_written_for_a_human_to_look_at()
    {
        var pages = Directory.GetFiles(Specimens, "*.xml").Order().Select(path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var body = Parse(name).Body;
            var old = name.EndsWith("_new", StringComparison.Ordinal)
                ? Parse(name.Replace("_new", "_old", StringComparison.Ordinal)).Body
                : body;
            return $"<h2 style=\"font:600 15px sans-serif\">{name}</h2>"
                   + LadderHtml.Fragment(LadderDiff.Compare(old, body), TiaPortalLadderParser.Style);
        });

        var page = $"<!doctype html><meta charset=utf-8><style>{LadderPageAssets.Css}</style>{string.Concat(pages)}";
        var path = Path.Combine(AppContext.BaseDirectory, "tia-ladder-gallery.html");
        File.WriteAllText(path, page);

        Assert.True(new FileInfo(path).Length > 1000);
    }
}
