using Versio.Core.Iec.Ladder;

namespace Versio.Core.Tests.Iec.Ladder;

/// <summary>
/// Builds neutral ladder networks for the tests: by hand for the named cases, and from a
/// series/parallel tree for the generated ones, so a test can state the shape it expects and
/// check the structure code recovers exactly that.
/// </summary>
internal static class LadderTestNetworks
{
    public static LdContact No(string id, string operand) => new(id, operand, LdContactKind.NormallyOpen);

    public static LdContact Nc(string id, string operand) => new(id, operand, LdContactKind.NormallyClosed);

    public static LdCoil Coil(string id, string operand, LdCoilKind kind = LdCoilKind.Normal) => new(id, operand, kind);

    /// <summary>A TON-shaped box: power in on IN, out on Q, operands on PT and ET.</summary>
    public static LdBox Timer(string id, string instance, string preset = "T#5s") => new(
        id,
        "TON",
        [
            new LdPin("IN", LdPinDirection.Input),
            new LdPin("PT", LdPinDirection.Input, preset),
            new LdPin("Q", LdPinDirection.Output),
            new LdPin("ET", LdPinDirection.Output, "#elapsed"),
        ],
        Instance: instance,
        Detail: "Time");

    /// <summary>The power-in and power-out pin names a test node uses.</summary>
    public static (string In, string Out) PowerPinsOf(LdNode node) =>
        node is LdBox ? ("IN", "Q") : (LdPins.In, LdPins.Out);

    /// <summary>
    /// The network a tree describes: the rail feeds the root, and the root ends the rung.
    /// Nodes are listed in reading order, which is the order a vendor file usually has them in.
    /// </summary>
    public static LadderNetwork FromTree(LdBlock root, int number = 1, string title = "", string comment = "")
    {
        var nodes = new List<LdNode> { new LdLeftRail("rail") };
        var nets = new List<List<LdPinRef>> { new() { new LdPinRef("rail", LdPins.Out) } };
        const int Sink = -1;

        void Wire(int net, LdPinRef end)
        {
            if (net != Sink)
            {
                nets[net].Add(end);
            }
        }

        int NewNet()
        {
            nets.Add([]);
            return nets.Count - 1;
        }

        void Build(LdBlock block, int from, int to)
        {
            switch (block)
            {
                case LdLeaf leaf:
                    nodes.Add(leaf.Node);
                    var (pin, pout) = PowerPinsOf(leaf.Node);
                    Wire(from, new LdPinRef(leaf.Node.Id, pin));
                    Wire(to, new LdPinRef(leaf.Node.Id, pout));
                    break;
                case LdSeries series:
                    var current = from;
                    for (var i = 0; i < series.Items.Count; i++)
                    {
                        var next = i == series.Items.Count - 1 ? to : NewNet();
                        Build(series.Items[i], current, next);
                        current = next;
                    }

                    break;
                case LdParallel parallel:
                    foreach (var branch in parallel.Branches)
                    {
                        Build(branch, from, to);
                    }

                    break;
            }
        }

        Build(root, 0, Sink);
        return new LadderNetwork
        {
            Number = number,
            Title = title,
            Comment = comment,
            Nodes = nodes,
            Nets = nets.Where(n => n.Count > 1).Select(n => new LdNet(n)).ToList(),
        };
    }

    public static LdBlock S(params LdBlock[] items) => new LdSeries(items);

    public static LdBlock P(params LdBlock[] branches) => new LdParallel(branches);

    public static LdBlock L(LdNode node) => new LdLeaf(node);

    /// <summary>A tree as text, to compare the shape the structure code found with the one built.</summary>
    public static string ShapeOf(LdBlock? block) => block switch
    {
        null => "",
        LdLeaf leaf => leaf.Node.Id,
        LdSeries s => "S(" + string.Join(",", s.Items.Select(ShapeOf)) + ")",
        LdParallel p => "P(" + string.Join("|", p.Branches.Select(ShapeOf)) + ")",
        _ => "?",
    };

    /// <summary>
    /// A random well-formed rung: contacts, timers and nested branches, ending in one coil or
    /// several in parallel, with operand names from short to far too long.
    /// </summary>
    public static LdBlock Random(Random random)
    {
        var id = 0;
        string Next() => $"n{++id}";

        string Operand()
        {
            var length = random.Next(10) switch
            {
                0 => 60,
                1 or 2 => 24,
                _ => random.Next(2, 12),
            };
            return "\"" + new string('a', length) + "\"";
        }

        LdBlock Element(int depth)
        {
            var roll = random.Next(depth >= 2 ? 6 : 8);
            return roll switch
            {
                0 => L(Timer(Next(), Operand())),
                1 or 2 => L(Nc(Next(), Operand())),
                6 or 7 => Branches(depth + 1, terminal: false),
                _ => L(No(Next(), Operand())),
            };
        }

        LdBlock Run(int depth, bool terminal)
        {
            var count = random.Next(1, 4);
            var items = Enumerable.Range(0, count).Select(_ => Element(depth)).ToList();
            if (terminal)
            {
                items.Add(random.Next(3) == 0 && depth < 2 ? Branches(depth + 1, terminal: true) : L(Coil(Next(), Operand())));
            }

            // A series of one is that one thing; a parallel never directly holds a parallel.
            return items.Count == 1 ? items[0] : new LdSeries(Flatten(items));
        }

        LdBlock Branches(int depth, bool terminal)
        {
            var count = random.Next(2, 4);
            return new LdParallel(Enumerable.Range(0, count).Select(_ =>
            {
                var branch = Run(depth, terminal);
                return branch is LdParallel ? new LdSeries([L(No(Next(), Operand())), branch]) : branch;
            }).ToList());
        }

        static List<LdBlock> Flatten(IEnumerable<LdBlock> items) =>
            items.SelectMany(i => i is LdSeries s ? s.Items : [i]).ToList();

        return Run(0, terminal: true);
    }
}
