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
    public void The_fixtures_cyclic_interrupt_reads_as_a_function_block_diagram()
    {
        // Until 2026-10-05 this returned null: same schema, different language, and drawing it
        // as ladder would have drawn something the IDE does not show. It is FBD, drawn as FBD.
        var path = Path.Combine(CyclicBlocks, "Cyclic interrupt.xml");

        var document = TiaPortalProjectFormat.Instance.TryParseLadder("x/Cyclic interrupt.xml", File.ReadAllText(path));

        Assert.NotNull(document);
        Assert.Equal(7, document.Body.Networks.Count);
        Assert.All(document.Body.Networks, n => Assert.Equal(LdLanguage.FunctionBlock, n.Language));
        var call = Assert.IsType<LdBox>(Assert.Single(document.Body.Networks[0].Nodes));
        Assert.Equal("Service", call.TypeName);
        Assert.Equal("FC", call.Detail);
    }

    [Fact]
    public void Fbd_gates_and_comparators_are_iec_boxes_with_their_inputs_in_number_order()
    {
        var path = Path.Combine(TestPaths.Fixtures, "projects", "CleaningStation_V20_V21", "CleaningStation_V20_V21",
            "ET 200SP station_2", "CleaningStation", "Program blocks", "02_ControlModules", "P100.xml");
        var nodes = TiaPortalProjectFormat.Instance.TryParseLadder("x/P100.xml", File.ReadAllText(path))!
            .Body.Networks.SelectMany(n => n.Nodes).ToList();

        var and = nodes.OfType<LdBox>().First(b => b.TypeName == "&");
        Assert.Equal(["in1", "in2"], and.Pins.Where(p => p.Direction == LdPinDirection.Input).Select(p => p.Name).Take(2));
        Assert.Contains(nodes.OfType<LdBox>(), b => b.TypeName == ">=1");
        Assert.Contains(nodes.OfType<LdBox>(), b => b.TypeName == "CMP <=" && b.Detail == "Real");
        Assert.Contains(nodes, n => n is LdCoil);
        Assert.DoesNotContain(nodes, n => n is LdUnsupported);
    }

    /// <summary>Every FBD block of the fixture: 21 blocks, 127 networks of Portal's own output.</summary>
    private static IEnumerable<(string Name, LadderDocument Document)> FixtureFbd()
    {
        var blocks = Path.Combine(TestPaths.Fixtures, "projects", "CleaningStation_V20_V21", "CleaningStation_V20_V21",
            "ET 200SP station_2", "CleaningStation", "Program blocks");
        foreach (var file in Directory.GetFiles(blocks, "*.xml", SearchOption.AllDirectories).Order())
        {
            var text = File.ReadAllText(file);
            if (text.Contains("<ProgrammingLanguage>FBD</ProgrammingLanguage>", StringComparison.Ordinal)
                && TiaPortalProjectFormat.Instance.TryParseLadder("x/" + Path.GetFileName(file), text) is { } document)
            {
                yield return (Path.GetFileNameWithoutExtension(file), document);
            }
        }
    }

    [Fact]
    public void Every_fbd_network_in_the_fixture_draws_within_the_shared_invariants()
    {
        var blocks = FixtureFbd().ToList();
        Assert.Equal(21, blocks.Count);

        var networks = 0;
        foreach (var (name, document) in blocks)
        {
            foreach (var network in document.Body.Networks)
            {
                networks++;
                Assert.DoesNotContain(network.Nodes, n => n is LdUnsupported);
                var drawing = LadderLayout.Lay(network, document.Style);
                Assert.Null(drawing.FallbackReason);
                var problems = LadderInvariants.Violations(drawing);
                Assert.True(problems.Count == 0, $"{name} network {network.Number}: {string.Join("; ", problems)}");
            }
        }

        Assert.True(networks >= 127, $"{networks} networks");
    }

    private static string FixtureBlock(string relative) => Path.Combine(TestPaths.Fixtures, "projects",
        "CleaningStation_V20_V21", "CleaningStation_V20_V21", "ET 200SP station_2", "CleaningStation",
        "Program blocks", relative.Replace('/', Path.DirectorySeparatorChar) + ".xml");

    [Theory]
    [InlineData("01_CyclicBlocks/Cyclic interrupt", "Fixture_CyclicInterrupt")]
    [InlineData("02_ControlModules/P100", "Fixture_P100")]
    [InlineData("05_Services/Service", "Fixture_Service")]
    public void Fixture_fbd_blocks_draw_as_approved(string block, string approvedAs)
    {
        // Portal's own output, so unlike the ladder specimens these are not guesses about the
        // schema. Looked at in the gallery before approving (2026-10-05).
        var document = TiaPortalProjectFormat.Instance.TryParseLadder("x/b.xml", File.ReadAllText(FixtureBlock(block)))!;
        var html = LadderHtml.Fragment(LadderDiff.Compare(document.Body, document.Body), document.Style);

        var folder = Path.Combine(Specimens, "Approved");
        var approved = Path.Combine(folder, approvedAs + ".approved.html");
        var received = Path.Combine(folder, approvedAs + ".received.html");
        if (File.Exists(approved) && File.ReadAllText(approved) == html)
        {
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, html);
        Assert.Fail($"{approvedAs}: the drawing differs from {approved}. Look at {received}; if it is right, copy it over the approved file.");
    }

    [Fact]
    public void An_fbd_operand_renamed_in_a_copy_of_p100_is_that_network_drawn_once_saying_what_it_was()
    {
        var original = File.ReadAllText(FixtureBlock("02_ControlModules/P100"));
        const string Tag = "<Component Name=\"StopAut\" />";
        var at = original.IndexOf(Tag, StringComparison.Ordinal);
        Assert.True(at >= 0);

        // The first use only: the tag is read in more than one network, and this test is about
        // one network changing while the rest stay as they were.
        var edited = string.Concat(original.AsSpan(0, at), "<Component Name=\"StopAuto\" />", original.AsSpan(at + Tag.Length));

        var old = TiaPortalProjectFormat.Instance.TryParseLadder("x/P100.xml", original)!.Body;
        var @new = TiaPortalProjectFormat.Instance.TryParseLadder("x/P100.xml", edited)!.Body;
        var comparison = LadderDiff.Compare(old, @new);

        var row = Assert.Single(comparison.Rows, r => r.Change != LadderChange.Same);
        Assert.Equal(LadderChange.Changed, row.Change);
        Assert.Contains(row.New!.Marks.Values, m => m == LdMark.Modified);

        // Nothing was removed, so the merged view draws the network once, the box that reads the
        // renamed tag saying what it read. The first FBD cut drew it twice (the owner, 2026-10-05).
        var html = LadderHtml.Fragment(comparison, TiaPortalLadderParser.Style);
        var start = html.IndexOf("data-change=\"changed\"", StringComparison.Ordinal);
        var section = html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        Assert.Equal(1, section.Split("<svg ").Length - 1);
        Assert.Contains("#StopAut", System.Net.WebUtility.HtmlDecode(section), StringComparison.Ordinal);
        Assert.Contains("class=\"ld-t-was\"", section, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gallery_of_the_fixtures_fbd_is_written_for_a_human_to_look_at()
    {
        var page = new System.Text.StringBuilder($"<!doctype html><meta charset=utf-8><style>{LadderPageAssets.Css} body{{background:var(--ld-bg)}}</style>");
        foreach (var (name, document) in FixtureFbd())
        {
            page.Append("<h2 style=\"font:600 15px sans-serif\">").Append(name).Append("</h2>")
                .Append(LadderHtml.Fragment(LadderDiff.Compare(document.Body, document.Body), document.Style));
        }

        var path = Path.Combine(AppContext.BaseDirectory, "tia-fbd-gallery.html");
        File.WriteAllText(path, page.ToString());
        Assert.True(new FileInfo(path).Length > 1000);
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
