using Versio.Core.Iec.Ladder;
using Versio.TestSupport;
using Xunit;
using static Versio.Core.Tests.Iec.Ladder.LadderTestNetworks;

namespace Versio.Core.Tests.Iec.Ladder;

/// <summary>
/// The structure code recovers the shape a rung was built with, and the layout draws it the way
/// a ladder editor would. The invariants run over every named case and 500 generated rungs,
/// because a layout bug is usually in a combination nobody thought to write down.
/// </summary>
public sealed class LadderLayoutTests
{
    private static LadderDrawing Lay(LadderNetwork network, LadderStyle? style = null) =>
        LadderLayout.Lay(network, style ?? LadderStyle.Default);

    private static LdGroup Element(LadderDrawing drawing, string key) =>
        drawing.Groups.Single(g => g.Key == key);

    [Fact]
    public void A_series_rung_is_found_in_order()
    {
        var network = FromTree(S(L(No("a", "Start")), L(Nc("b", "Stop")), L(Coil("c", "Motor"))));

        var shape = LadderStructure.Of(network);

        Assert.True(shape.IsSeriesParallel);
        Assert.Equal("S(a,b,c)", ShapeOf(shape.Root));
    }

    [Fact]
    public void An_or_branch_is_found_and_its_contacts_stand_in_one_column()
    {
        var network = FromTree(S(P(L(No("a", "Start")), L(No("b", "Seal"))), L(Coil("c", "Motor"))));

        var drawing = Lay(network);

        Assert.Equal("S(P(a|b),c)", ShapeOf(LadderStructure.Of(network).Root));
        var a = Element(drawing, "a").Bounds;
        var b = Element(drawing, "b").Bounds;
        Assert.Equal(a.X, b.X);
        Assert.True(b.Y >= a.Bottom, "the second branch is drawn below the first");
        Assert.True(Element(drawing, "c").Bounds.X >= Math.Max(a.Right, b.Right), "the coil is right of both");
    }

    [Fact]
    public void Coils_at_the_end_of_parallel_branches_line_up_on_the_right()
    {
        var network = FromTree(S(
            L(No("a", "Run")),
            P(S(L(No("b", "Fast")), L(Coil("c", "HighSpeed"))), L(Coil("d", "Lamp", LdCoilKind.Set)))));

        var drawing = Lay(network);

        Assert.Equal("S(a,P(S(b,c)|d))", ShapeOf(LadderStructure.Of(network).Root));
        Assert.Equal(Element(drawing, "c").Bounds.Right, Element(drawing, "d").Bounds.Right, 2);
        Assert.Equal(drawing.Width - 4, Element(drawing, "c").Bounds.Right, 2);
    }

    [Fact]
    public void A_timer_box_sits_on_the_rung_with_its_preset_as_a_label()
    {
        var network = FromTree(S(L(No("a", "Start")), L(Timer("t", "\"Delay_DB\"")), L(Coil("c", "Done"))));

        var drawing = Lay(network);
        var timer = Element(drawing, "t");

        Assert.Equal("box", timer.Kind);
        var texts = timer.Items.OfType<LdText>().ToList();
        Assert.Contains(texts, t => t.Text == "TON" && t.Role == "type");
        Assert.Contains(texts, t => t.Text == "\"Delay_DB\"" && t.Role == "instance");
        Assert.Contains(texts, t => t.Text == "T#5s" && t.Role == "operand");

        // The rung runs straight through: the contact's anchor, the timer's IN row and the
        // coil's anchor are one horizontal line.
        var contactY = Element(drawing, "a").Items.OfType<LdLine>().First().Y1;
        var coilY = Element(drawing, "c").Items.OfType<LdLine>().First().Y1;
        var timerInY = timer.Items.OfType<LdLine>().First(l => Math.Abs(l.X1 - timer.Bounds.X) < 0.01).Y1;
        Assert.Equal(contactY, timerInY, 2);
        Assert.Equal(contactY, coilY, 2);
    }

    [Fact]
    public void A_long_operand_is_cut_and_keeps_its_full_name_for_the_hover()
    {
        var name = "\"" + new string('x', 60) + "\"";
        var drawing = Lay(FromTree(S(L(No("a", name)), L(Coil("c", "Out")))));

        var text = Element(drawing, "a").Items.OfType<LdText>().Single(t => t.Role == "operand");
        Assert.EndsWith("…", text.Text, StringComparison.Ordinal);
        Assert.Equal(name, text.Full);
        AssertInvariants(drawing, "long operand");
    }

    [Fact]
    public void A_network_without_a_series_parallel_shape_still_draws_every_element()
    {
        // A box fed power on two pins: no rung tree can hold it.
        var box = new LdBox("x", "CTUD",
        [
            new LdPin("CU", LdPinDirection.Input),
            new LdPin("CD", LdPinDirection.Input),
            new LdPin("QU", LdPinDirection.Output),
        ]);
        var network = new LadderNetwork
        {
            Number = 1,
            Nodes = [new LdLeftRail("rail"), No("a", "Up"), No("b", "Down"), box, Coil("c", "Full")],
            Nets =
            [
                new LdNet([new("rail", LdPins.Out), new("a", LdPins.In), new("b", LdPins.In)]),
                new LdNet([new("a", LdPins.Out), new("x", "CU")]),
                new LdNet([new("b", LdPins.Out), new("x", "CD")]),
                new LdNet([new("x", "QU"), new("c", LdPins.In)]),
            ],
        };

        var drawing = Lay(network);

        Assert.NotNull(drawing.FallbackReason);
        Assert.Equal(["a", "b", "c", "x"], drawing.Groups.Where(g => g.Key.Length > 0).Select(g => g.Key).Order());
        Assert.True(Element(drawing, "x").Bounds.X > Element(drawing, "a").Bounds.Right, "the box is a column after its inputs");
    }

    [Fact(Timeout = 10_000)]
    public async Task An_element_wired_to_itself_is_refused_rather_than_reduced_forever()
    {
        // Async for the Timeout only: the unfixed reduction never returns.
        await Task.Yield();
        // Contact b takes power from the same net it gives power to: an edge from a net to
        // itself. The series step then found that edge as both the one into the point and the
        // one out of it, "merged" it with itself into a series twice as long, removed one
        // edge, and went round again — the series doubled every pass until memory ran out,
        // from three nodes of malformed vendor XML (the 2026-10-24 review's item 33).
        var network = new LadderNetwork
        {
            Number = 1,
            Nodes = [new LdLeftRail("rail"), No("a", "Start"), No("b", "Loop")],
            Nets =
            [
                new LdNet([new("rail", LdPins.Out), new("a", LdPins.In)]),
                new LdNet([new("b", LdPins.In), new("b", LdPins.Out)]),
            ],
        };

        var structure = LadderStructure.Of(network);

        Assert.Null(structure.Root);
        Assert.Contains("itself", structure.FallbackReason, StringComparison.Ordinal);

        var drawing = Lay(network);
        Assert.NotNull(drawing.FallbackReason);
        Assert.Equal(["a", "b"], drawing.Groups.Where(g => g.Key.Length > 0).Select(g => g.Key).Order());
    }

    [Fact]
    public void An_empty_network_is_a_rail()
    {
        var drawing = Lay(new LadderNetwork { Number = 1 });

        Assert.Single(drawing.Groups, g => g.Kind == "rail");
        Assert.DoesNotContain(drawing.Groups, g => g.Key.Length > 0);
    }

    [Fact]
    public void A_network_in_another_language_is_a_placeholder_that_says_so()
    {
        var drawing = Lay(new LadderNetwork { Number = 4, UnsupportedLanguage = "SCL" });

        var text = drawing.Groups.Single(g => g.Kind == "placeholder").Items.OfType<LdText>().Single();
        Assert.StartsWith("SCL network", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_right_rail_is_drawn_when_the_vendor_draws_one_and_the_coil_reaches_it()
    {
        var drawing = Lay(
            FromTree(S(L(No("a", "In")), L(Coil("c", "Out")))),
            new LadderStyle { RightRailDrawn = true });

        var rails = drawing.Groups.Where(g => g.Kind == "rail").ToList();
        Assert.Equal(2, rails.Count);
        Assert.Equal(rails[1].Bounds.X, Element(drawing, "c").Bounds.Right, 2);
    }

    [Fact]
    public void The_same_network_always_gives_the_same_drawing()
    {
        var network = FromTree(Random(new Random(7)));

        Assert.Equal(Lay(network), Lay(network), DrawingComparer.Instance);
    }

    [Fact]
    public void Generated_rungs_keep_their_shape_and_every_invariant()
    {
        // Seeded, so a failure names the seed and reproduces.
        for (var seed = 0; seed < 500; seed++)
        {
            var tree = Random(new Random(seed));
            var network = FromTree(tree);

            var shape = LadderStructure.Of(network);
            Assert.True(shape.IsSeriesParallel, $"seed {seed}: {shape.FallbackReason}");
            Assert.Equal(ShapeOf(tree), ShapeOf(shape.Root));

            AssertInvariants(Lay(network), $"seed {seed}");
        }
    }

    [Fact]
    public void A_wide_rung_and_a_wide_box_keep_every_invariant()
    {
        // Twenty contacts in series, then a box with twelve pins: the drawing grows sideways
        // and the page scrolls it, rather than squeezing labels together.
        var box = new LdBox("box", "WIDE", Enumerable.Range(0, 12)
            .Select(i => new LdPin(i == 0 ? "en" : $"in{i}", LdPinDirection.Input, i == 0 ? null : $"#value_{i}"))
            .Append(new LdPin("eno", LdPinDirection.Output))
            .ToList());
        var items = Enumerable.Range(0, 20).Select(i => L(No($"c{i}", $"Contact_{i}"))).ToList();
        var network = FromTree(new LdSeries([.. items, L(box), L(Coil("q", "Out"))]));
        var fixedBox = network with
        {
            Nets = network.Nets
                .Select(n => new LdNet(n.Ends.Select(e => e.NodeId == "box"
                    ? new LdPinRef("box", e.Pin == "IN" ? "en" : "eno")
                    : e).ToList()))
                .ToList(),
        };

        var drawing = Lay(fixedBox);

        AssertInvariants(drawing, "wide");
        Assert.True(drawing.Width > 20 * 88, "twenty contacts are not squeezed into less than their cells");
    }

    [Fact]
    public void Ten_nested_branches_keep_every_invariant()
    {
        LdBlock Nest(int depth) => depth == 0
            ? L(No("leaf", "Deepest"))
            : P(S(L(No($"a{depth}", $"A_{depth}")), Nest(depth - 1)), L(No($"b{depth}", $"B_{depth}")));

        var drawing = Lay(FromTree(S(Nest(10), L(Coil("q", "Out")))));

        AssertInvariants(drawing, "tall");
    }

    [Fact]
    public void A_block_of_120_networks_compares_and_draws_quickly()
    {
        // The plan's bound for the pane is 300 ms. The test allows ten times that, because a
        // test runner on a loaded machine is not the measurement; the log line is. A regression
        // to quadratic-in-networks would still blow through it.
        var networks = Enumerable.Range(1, 120)
            .Select(i => FromTree(S(L(No($"a{i}", $"Start_{i}")), P(L(No($"b{i}", $"Seal_{i}")), L(Nc($"c{i}", $"Stop_{i}"))),
                L(Timer($"t{i}", $"\"Delay_{i}\"")), L(Coil($"q{i}", $"Out_{i}"))), i))
            .ToList();
        var old = new LadderBody("Long", networks);
        var @new = new LadderBody("Long", networks.Select((n, i) => i % 10 == 0 ? Renamed(n) : n).ToList());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var html = LadderHtml.Fragment(LadderDiff.Compare(old, @new), LadderStyle.Default);
        clock.Stop();

        Assert.Contains("12 changed", html, StringComparison.Ordinal);
        Assert.True(clock.ElapsedMilliseconds < 3000, $"120 networks took {clock.ElapsedMilliseconds} ms");

        static LadderNetwork Renamed(LadderNetwork n) => n with
        {
            Nodes = n.Nodes.Select(x => x is LdCoil c ? c with { Operand = c.Operand + "_v2" } : x).ToList(),
        };
    }

    [Fact]
    public void The_invariants_catch_an_overlap()
    {
        // The check checks something: two elements drawn on top of each other must fail it.
        var drawing = Lay(FromTree(S(L(No("a", "A")), L(Coil("c", "C")))));
        var a = Element(drawing, "a");
        var broken = drawing with { Groups = [.. drawing.Groups, a with { Key = "a2" }] };

        Assert.ThrowsAny<Exception>(() => AssertInvariants(broken, "broken"));
    }

    /// <summary>
    /// The shared rules (<see cref="LadderInvariants"/>): nothing overlaps, every label stays
    /// inside its element, nothing sits left of the rail or outside the drawing, and every wire
    /// end touches something.
    /// </summary>
    internal static void AssertInvariants(LadderDrawing drawing, string context)
    {
        var problems = LadderInvariants.Violations(drawing);
        Assert.True(problems.Count == 0, $"{context}: {string.Join("; ", problems)}");
    }

    /// <summary>Records holding lists compare by reference; drawings are compared by content.</summary>
    private sealed class DrawingComparer : IEqualityComparer<LadderDrawing>
    {
        public static DrawingComparer Instance { get; } = new();

        public bool Equals(LadderDrawing? x, LadderDrawing? y) =>
            x is not null && y is not null
            && x.Width == y.Width && x.Height == y.Height
            && x.Groups.Count == y.Groups.Count
            && x.Groups.Zip(y.Groups).All(p =>
                p.First.Key == p.Second.Key && p.First.Bounds == p.Second.Bounds
                && p.First.Items.SequenceEqual(p.Second.Items));

        public int GetHashCode(LadderDrawing obj) => obj.Groups.Count;
    }
}
