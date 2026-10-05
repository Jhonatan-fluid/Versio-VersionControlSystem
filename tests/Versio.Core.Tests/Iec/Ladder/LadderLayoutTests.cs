using Versio.Core.Iec.Ladder;
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
    public void The_invariants_catch_an_overlap()
    {
        // The check checks something: two elements drawn on top of each other must fail it.
        var drawing = Lay(FromTree(S(L(No("a", "A")), L(Coil("c", "C")))));
        var a = Element(drawing, "a");
        var broken = drawing with { Groups = [.. drawing.Groups, a with { Key = "a2" }] };

        Assert.ThrowsAny<Exception>(() => AssertInvariants(broken, "broken"));
    }

    /// <summary>
    /// What every drawing must satisfy, whatever its shape: nothing overlaps, every label stays
    /// inside its element, nothing sits left of the rail or outside the drawing, and every wire
    /// end touches something.
    /// </summary>
    internal static void AssertInvariants(LadderDrawing drawing, string context)
    {
        var elements = drawing.Groups.Where(g => g.Key.Length > 0).ToList();
        var all = new LdRect(0, 0, drawing.Width, drawing.Height);

        for (var i = 0; i < elements.Count; i++)
        {
            for (var j = i + 1; j < elements.Count; j++)
            {
                Assert.False(
                    elements[i].Bounds.Overlaps(elements[j].Bounds),
                    $"{context}: {elements[i].Key} overlaps {elements[j].Key}");
            }

            foreach (var text in elements[i].Items.OfType<LdText>())
            {
                var b = text.Bounds;
                Assert.True(
                    elements[i].Bounds.Contains(b.X, b.Y) && elements[i].Bounds.Contains(b.Right, b.Bottom),
                    $"{context}: '{text.Text}' runs outside {elements[i].Key}");
            }
        }

        var lines = drawing.Groups.SelectMany(g => g.Items.OfType<LdLine>().Select(l => (Group: g, Line: l))).ToList();
        foreach (var (group, line) in lines)
        {
            Assert.True(
                all.Contains(line.X1, line.Y1) && all.Contains(line.X2, line.Y2),
                $"{context}: a {group.Kind} line leaves the drawing");
            Assert.True(Math.Min(line.X1, line.X2) >= 4 - 0.01, $"{context}: a {group.Kind} line is left of the rail");
        }

        // Every wire end touches another line, and every element's input lead starts on one:
        // a wire that ends in empty space is a connection the diagram invented or lost.
        foreach (var (group, line) in lines.Where(l => l.Group.Kind == "wire"))
        {
            foreach (var (x, y) in new[] { (line.X1, line.Y1), (line.X2, line.Y2) })
            {
                Assert.True(
                    lines.Any(other => !ReferenceEquals(other.Line, line) && OnSegment(other.Line, x, y)),
                    $"{context}: a wire end at ({x:0.#}, {y:0.#}) touches nothing");
            }
        }

        foreach (var element in elements)
        {
            var leads = element.Items.OfType<LdLine>()
                .Where(l => Math.Abs(l.X1 - element.Bounds.X) < 0.01 && Math.Abs(l.Y1 - l.Y2) < 0.01);
            foreach (var lead in leads)
            {
                Assert.True(
                    lines.Any(other => !ReferenceEquals(other.Group, element) && OnSegment(other.Line, lead.X1, lead.Y1)),
                    $"{context}: {element.Key}'s input at ({lead.X1:0.#}, {lead.Y1:0.#}) is not connected");
            }
        }
    }

    private static bool OnSegment(LdLine l, double x, double y)
    {
        const double e = 0.01;
        return x >= Math.Min(l.X1, l.X2) - e && x <= Math.Max(l.X1, l.X2) + e
            && y >= Math.Min(l.Y1, l.Y2) - e && y <= Math.Max(l.Y1, l.Y2) + e
            && Math.Abs(((l.X2 - l.X1) * (y - l.Y1)) - ((l.Y2 - l.Y1) * (x - l.X1))) < 0.5;
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
