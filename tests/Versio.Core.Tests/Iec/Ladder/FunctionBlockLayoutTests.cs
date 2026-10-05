using Versio.Core.Iec.Ladder;
using Versio.TestSupport;
using Xunit;

namespace Versio.Core.Tests.Iec.Ladder;

/// <summary>
/// Function block diagrams laid out left to right: every box right of what feeds it, inputs
/// level with their feeders, and no two wires sharing a vertical run where they would read as
/// one joined bus.
/// </summary>
public sealed class FunctionBlockLayoutTests
{
    /// <summary>An expression: a box with inputs that are operands or other expressions.</summary>
    private sealed record Expr(string Type, IReadOnlyList<object> Inputs);

    /// <summary>Builds the network an expression tree describes, rooted in an assignment.</summary>
    private static LadderNetwork Network(Expr root, string target = "Out", int number = 1)
    {
        var nodes = new List<LdNode>();
        var nets = new List<LdNet>();
        var next = 0;

        string Build(Expr expr)
        {
            var id = $"b{++next}";
            var pins = new List<LdPin>();
            for (var i = 0; i < expr.Inputs.Count; i++)
            {
                var name = $"in{i + 1}";
                switch (expr.Inputs[i])
                {
                    case string operand:
                        pins.Add(new LdPin(name, LdPinDirection.Input, operand));
                        break;
                    case Expr child:
                        var childId = Build(child);
                        pins.Add(new LdPin(name, LdPinDirection.Input));
                        nets.Add(new LdNet([new LdPinRef(childId, "out"), new LdPinRef(id, name)]));
                        break;
                }
            }

            pins.Add(new LdPin("out", LdPinDirection.Output));
            nodes.Add(new LdBox(id, expr.Type, pins));
            return id;
        }

        var top = Build(root);
        nodes.Add(new LdCoil("assign", target, LdCoilKind.Normal));
        nets.Add(new LdNet([new LdPinRef(top, "out"), new LdPinRef("assign", LdPins.In)]));
        return new LadderNetwork { Number = number, Nodes = nodes, Nets = nets, Language = LdLanguage.FunctionBlock };
    }

    private static Expr Random(Random random, int depth = 0)
    {
        var count = random.Next(2, 6);
        var inputs = Enumerable.Range(0, count).Select(i => depth < 3 && random.Next(3) == 0
            ? (object)Random(random, depth + 1)
            : $"\"Tag_{depth}_{i}_{random.Next(1000)}\"").ToList();
        var type = random.Next(4) switch
        {
            0 => ">=1",
            1 => "CMP ==",
            _ => "&",
        };
        return new Expr(type, inputs);
    }

    private static LadderDrawing Lay(LadderNetwork network) => LadderLayout.Lay(network, LadderStyle.Default);

    private static LdRect Bounds(LadderDrawing drawing, string key) => drawing.Groups.Single(g => g.Key == key).Bounds;

    [Fact]
    public void A_gate_fed_by_comparators_stands_right_of_them_with_the_assignment_last()
    {
        var network = Network(new Expr("&", [new Expr("CMP ==", ["#a", "1"]), new Expr("CMP ==", ["#b", "2"]), "\"Enable\""]));

        var drawing = Lay(network);

        var gate = Bounds(drawing, "b1");
        Assert.True(gate.X > Bounds(drawing, "b2").Right && gate.X > Bounds(drawing, "b3").Right);
        Assert.True(Bounds(drawing, "assign").X > gate.Right);
        Assert.DoesNotContain(drawing.Groups, g => g.Kind == "rail");
        Assert.Null(drawing.FallbackReason);
    }

    [Fact]
    public void Every_input_wire_is_straight_when_the_feeders_are_stacked()
    {
        // The bus the fixture's Service block showed: seven comparators into one OR, every wire
        // bending at the same x. With the gate's rows placed at its feeders, every wire is one line.
        var feeders = Enumerable.Range(1, 7).Select(i => (object)new Expr("CMP ==", [$"#state_{i}", $"{i}"])).ToList();
        var drawing = Lay(Network(new Expr(">=1", feeders)));

        var wires = drawing.Groups.Where(g => g.Kind == "wire").SelectMany(g => g.Items.OfType<LdLine>()).ToList();
        Assert.All(wires, w => Assert.Equal(w.Y1, w.Y2, 2));
    }

    [Fact]
    public void An_fbd_network_draws_no_rail_and_states_no_fallback()
    {
        var drawing = Lay(Network(new Expr("&", ["\"A\"", "\"B\""])));

        Assert.DoesNotContain(drawing.Groups, g => g.Kind == "rail");
        Assert.Null(drawing.FallbackReason);
    }

    [Fact]
    public void Generated_expressions_keep_every_invariant_and_never_draw_a_bus()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var network = Network(Random(new Random(seed)));
            var drawing = Lay(network);

            var problems = LadderInvariants.Violations(drawing);
            Assert.True(problems.Count == 0, $"seed {seed}: {string.Join("; ", problems)}");

            // Every box stands right of every box that feeds it.
            foreach (var net in network.Nets)
            {
                var from = Bounds(drawing, net.Ends[0].NodeId);
                var to = Bounds(drawing, net.Ends[1].NodeId);
                Assert.True(to.X > from.Right, $"seed {seed}: {net.Ends[1].NodeId} is not right of its feeder");
            }

            AssertNoBus(drawing, $"seed {seed}");
        }
    }

    [Fact]
    public void The_bus_check_catches_two_wires_sharing_a_vertical_run()
    {
        // The check checks something: two different wires laid on the same x, overlapping in y.
        var drawing = new LadderDrawing(100, 100,
        [
            new LdGroup("", "wire", new LdRect(0, 0, 100, 100),
            [
                new LdLine(10, 10, 50, 10), new LdLine(50, 10, 50, 60), new LdLine(50, 60, 90, 60),
                new LdLine(10, 30, 50, 30), new LdLine(50, 30, 50, 80), new LdLine(50, 80, 90, 80),
            ]),
        ], null);

        Assert.ThrowsAny<Exception>(() => AssertNoBus(drawing, "made up"));
    }

    /// <summary>
    /// No two vertical wire runs overlap at the same x. Each wire of the layout is its own
    /// connection (one output to one input), so an overlap draws two connections as one joined
    /// line, which is a connection the diagram invented.
    /// </summary>
    private static void AssertNoBus(LadderDrawing drawing, string context)
    {
        var verticals = drawing.Groups.Where(g => g.Kind == "wire")
            .SelectMany(g => g.Items.OfType<LdLine>())
            .Where(l => Math.Abs(l.X1 - l.X2) < 0.01 && Math.Abs(l.Y1 - l.Y2) > 0.01)
            .Select(l => (X: l.X1, Top: Math.Min(l.Y1, l.Y2), Bottom: Math.Max(l.Y1, l.Y2)))
            .ToList();

        for (var i = 0; i < verticals.Count; i++)
        {
            for (var j = i + 1; j < verticals.Count; j++)
            {
                var (a, b) = (verticals[i], verticals[j]);
                Assert.False(
                    Math.Abs(a.X - b.X) < 0.01 && a.Top < b.Bottom - 0.01 && b.Top < a.Bottom - 0.01,
                    $"{context}: two wires share a vertical run at x={a.X:0.#}");
            }
        }
    }
}
