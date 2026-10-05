using System.Text;

namespace Versio.Core.Iec.Ladder;

/// <summary>A network's power flow as the IDE draws it: elements in series and in parallel.</summary>
public abstract record LdBlock;

public sealed record LdLeaf(LdNode Node) : LdBlock;

public sealed record LdSeries(IReadOnlyList<LdBlock> Items) : LdBlock;

public sealed record LdParallel(IReadOnlyList<LdBlock> Branches) : LdBlock;

/// <summary>
/// What <see cref="LadderStructure.Of"/> recovered from one network.
/// </summary>
/// <param name="Root">The series/parallel tree, or null when the network is empty or has no such shape.</param>
/// <param name="Elements">Every drawn element in reading order: left to right, then top to bottom.</param>
/// <param name="Labels">Jump targets, which stand at the head of the network wired to nothing.</param>
/// <param name="FallbackReason">Why no series/parallel shape was found; null when one was, or the network is empty.</param>
public sealed record LadderShape(
    LdBlock? Root,
    IReadOnlyList<LdNode> Elements,
    IReadOnlyList<LdLabel> Labels,
    string? FallbackReason)
{
    public bool IsSeriesParallel => FallbackReason is null;
}

/// <summary>
/// Recovers the series/parallel shape of a network from its parts and wires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reduce a graph instead of walking it.</b> Every ladder editor draws a rung as
/// elements in series, with branches in parallel, and a well-formed rung is exactly a
/// two-terminal series/parallel graph: the left rail on one side, the coils on the other. The
/// classic reduction finds that shape whatever order the vendor wrote the parts in. Two edges
/// between the same two points become one parallel block, and a point with exactly one
/// edge in and one edge out becomes one series block. Repeat until one edge is left. A walk
/// from the rail would have to guess where each branch closes again; the reduction never
/// guesses.
/// </para>
/// <para>
/// <b>Where the rung ends.</b> Coils, jumps and returns leave their output unwired, and so does
/// a box whose enable-out goes nowhere. Every such end is the same point, the sink, which is
/// what lets two coils in parallel at the end of a rung reduce to one parallel block.
/// </para>
/// <para>
/// <b>When there is no such shape</b> (a box fed power on two pins, an open wire), the
/// network keeps its parts and its wires, and the layout places them by rank instead. That
/// is worse to look at and still correct: the reason is kept so that it reaches the log, and
/// the drawing never pretends the network is simpler than it is.
/// </para>
/// </remarks>
public static class LadderStructure
{
    public static LadderShape Of(LadderNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        var labels = network.Nodes.OfType<LdLabel>().ToList();
        var drawn = network.Nodes.Where(n => n is not (LdLeftRail or LdLabel)).ToList();

        if (drawn.Count == 0)
        {
            return new LadderShape(null, [], labels, null);
        }

        var root = Reduce(network, drawn, out var reason);
        return root is null
            ? new LadderShape(null, drawn, labels, reason)
            : new LadderShape(root, Walk(root).ToList(), labels, null);
    }

    /// <summary>Every element of a block, left to right, then top to bottom.</summary>
    public static IEnumerable<LdNode> Walk(LdBlock block)
    {
        switch (block)
        {
            case LdLeaf leaf:
                yield return leaf.Node;
                break;
            case LdSeries series:
                foreach (var node in series.Items.SelectMany(Walk))
                {
                    yield return node;
                }

                break;
            case LdParallel parallel:
                foreach (var node in parallel.Branches.SelectMany(Walk))
                {
                    yield return node;
                }

                break;
        }
    }

    /// <summary>
    /// The pins of a node that carry power flow in, and out, in the order the node lists them.
    /// </summary>
    /// <remarks>
    /// A box's power pins are the ones wired to something; the rest are operands. The
    /// two-terminal elements always use <see cref="LdPins"/>, so they are read the same way
    /// whatever the vendor called their pins.
    /// </remarks>
    public static (IReadOnlyList<string> In, IReadOnlyList<string> Out) PowerPins(
        LdNode node, ISet<LdPinRef> wired)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(wired);

        IReadOnlyList<LdPin>? pins = node switch
        {
            LdBox box => box.Pins,
            LdUnsupported other => other.Pins,
            _ => null,
        };

        if (pins is null)
        {
            return node switch
            {
                LdLeftRail => ([], [LdPins.Out]),
                LdLabel => ([], []),
                _ => (Wired(LdPins.In), Wired(LdPins.Out)),
            };
        }

        var ins = pins
            .Where(p => p.Direction != LdPinDirection.Output && wired.Contains(new LdPinRef(node.Id, p.Name)))
            .Select(p => p.Name)
            .ToList();
        var outs = pins
            .Where(p => p.Direction == LdPinDirection.Output && wired.Contains(new LdPinRef(node.Id, p.Name)))
            .Select(p => p.Name)
            .ToList();
        return (ins, outs);

        IReadOnlyList<string> Wired(string pin) =>
            wired.Contains(new LdPinRef(node.Id, pin)) ? [pin] : [];
    }

    private sealed record Edge(int From, int To, LdBlock Block, int Order);

    private static LdBlock? Reduce(LadderNetwork network, List<LdNode> drawn, out string? reason)
    {
        var netOf = new Dictionary<LdPinRef, int>();
        for (var i = 0; i < network.Nets.Count; i++)
        {
            foreach (var end in network.Nets[i].Ends)
            {
                netOf[end] = i;
            }
        }

        var wired = new HashSet<LdPinRef>(netOf.Keys);
        var rail = network.Nodes.OfType<LdLeftRail>().FirstOrDefault();
        if (rail is null || !netOf.TryGetValue(new LdPinRef(rail.Id, LdPins.Out), out var source))
        {
            reason = "no wired left rail";
            return null;
        }

        // A net is consumed when some node takes power from it. One nobody consumes is the
        // end of the rung, the same as an output wired to nothing at all.
        var consumed = new HashSet<int>();
        foreach (var node in network.Nodes)
        {
            foreach (var pin in PowerPins(node, wired).In)
            {
                consumed.Add(netOf[new LdPinRef(node.Id, pin)]);
            }
        }

        var sink = network.Nets.Count;
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < drawn.Count; i++)
        {
            position[drawn[i].Id] = i;
        }

        var edges = new List<Edge>();
        for (var order = 0; order < drawn.Count; order++)
        {
            var node = drawn[order];
            var (ins, outs) = PowerPins(node, wired);
            if (ins.Count != 1)
            {
                reason = ins.Count == 0
                    ? $"element {node.Id} takes no power"
                    : $"element {node.Id} takes power on {ins.Count} pins";
                return null;
            }

            if (outs.Count > 1)
            {
                reason = $"element {node.Id} gives power on {outs.Count} pins";
                return null;
            }

            var from = netOf[new LdPinRef(node.Id, ins[0])];
            var to = outs.Count == 0 ? sink : netOf[new LdPinRef(node.Id, outs[0])];
            if (to != sink && !consumed.Contains(to))
            {
                to = sink;
            }

            edges.Add(new Edge(from, to, new LdLeaf(node), order));
        }

        bool changed;
        do
        {
            changed = false;

            var parallel = edges.GroupBy(e => (e.From, e.To)).FirstOrDefault(g => g.Count() > 1);
            if (parallel is not null)
            {
                // Branches merge a pair at a time, so a branch block built earlier must not keep
                // its branches ahead of one that reads above them: every branch is ordered by
                // its first element in the vendor's own order, which is top to bottom.
                var members = parallel.OrderBy(e => e.Order).ToList();
                var branches = members
                    .SelectMany(e => e.Block is LdParallel p ? p.Branches : [e.Block])
                    .OrderBy(b => Walk(b).Min(n => position[n.Id]))
                    .ToList();
                var merged = new Edge(parallel.Key.From, parallel.Key.To, new LdParallel(branches), members[0].Order);
                edges.RemoveAll(members.Contains);
                edges.Add(merged);
                changed = true;
                continue;
            }

            foreach (var point in edges.Select(e => e.To).Distinct().ToList())
            {
                if (point == source || point == sink)
                {
                    continue;
                }

                var into = edges.Where(e => e.To == point).ToList();
                var outOf = edges.Where(e => e.From == point).ToList();
                if (into.Count != 1 || outOf.Count != 1)
                {
                    continue;
                }

                var (a, b) = (into[0], outOf[0]);
                var items = new List<LdBlock>();
                items.AddRange(a.Block is LdSeries sa ? sa.Items : [a.Block]);
                items.AddRange(b.Block is LdSeries sb ? sb.Items : [b.Block]);
                edges.Remove(a);
                edges.Remove(b);
                edges.Add(new Edge(a.From, b.To, new LdSeries(items), Math.Min(a.Order, b.Order)));
                changed = true;
                break;
            }
        }
        while (changed);

        if (edges.Count == 1 && edges[0].From == source && edges[0].To == sink)
        {
            reason = null;
            return edges[0].Block;
        }

        reason = $"power flow does not reduce to series and parallel ({edges.Count} pieces left)";
        return null;
    }

    /// <summary>
    /// One element as comparable text: its kind, its variant and its operands, never its id.
    /// </summary>
    /// <remarks>
    /// Ids are local to one file (TIA Portal's UIds are numbered per document), so two exports of
    /// the same rung give different ids. Comparing ids would mark every network changed.
    /// </remarks>
    public static string Describe(LdNode node) => node switch
    {
        LdContact c => $"contact:{c.Kind}:{c.Operand}:{c.EdgeMemory}",
        LdCoil c => $"coil:{c.Kind}:{c.Operand}:{c.EdgeMemory}",
        LdBox b => $"box:{b.TypeName}:{b.Detail}:{b.Instance}:{b.InlineCompare}:{Pins(b.Pins)}",
        LdJump j => $"jump:{j.IfNot}:{j.Target}",
        LdReturn => "return",
        LdLabel l => $"label:{l.Name}",
        LdUnsupported u => $"unsupported:{u.VendorName}:{Pins(u.Pins)}",
        LdLeftRail => "rail",
        _ => node.GetType().Name,
    };

    /// <summary>An element's kind and variant without its operands: what it is, not what it reads.</summary>
    public static string Shape(LdNode node) => node switch
    {
        LdContact c => $"contact:{c.Kind}",
        LdCoil c => $"coil:{c.Kind}",
        LdBox b => $"box:{b.TypeName}",
        LdJump j => $"jump:{j.IfNot}",
        LdUnsupported u => $"unsupported:{u.VendorName}",
        _ => Describe(node),
    };

    /// <summary>The operands an element reads or writes.</summary>
    public static IEnumerable<string> Operands(LdNode node) => node switch
    {
        LdContact c => [c.Operand],
        LdCoil c => [c.Operand],
        LdBox b => b.Pins.Where(p => p.Operand is not null).Select(p => p.Operand!)
            .Concat(b.Instance is null ? [] : [b.Instance]),
        LdJump j => [j.Target],
        LdUnsupported u => u.Pins.Where(p => p.Operand is not null).Select(p => p.Operand!),
        _ => [],
    };

    /// <summary>
    /// The whole network as comparable text: equal exactly when the IDE would draw the same
    /// network.
    /// </summary>
    public static string Fingerprint(LadderNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        var shape = Of(network);
        var text = new StringBuilder()
            .Append(network.Title).Append('\u001f')
            .Append(network.Comment).Append('\u001f')
            .Append(network.UnsupportedLanguage).Append('\u001f')
            .Append(network.Language).Append('\u001f')
            .AppendJoin(',', shape.Labels.Select(l => l.Name)).Append('\u001f');

        if (shape.Root is not null)
        {
            Serialise(text, shape.Root);
        }
        else
        {
            // No series/parallel shape: compare the parts and the wires, each named by what
            // it connects rather than by id, and sorted so that file order does not count.
            var byId = network.Nodes.ToDictionary(n => n.Id, Describe, StringComparer.Ordinal);
            text.Append("G[")
                .AppendJoin(';', shape.Elements.Select(Describe).Order(StringComparer.Ordinal))
                .Append('|')
                .AppendJoin(';', network.Nets
                    .Select(net => string.Join('+', net.Ends
                        .Select(e => $"{(byId.TryGetValue(e.NodeId, out var d) ? d : "?")}.{e.Pin}")
                        .Order(StringComparer.Ordinal)))
                    .Order(StringComparer.Ordinal))
                .Append(']');
        }

        return text.ToString();
    }

    private static void Serialise(StringBuilder text, LdBlock block)
    {
        switch (block)
        {
            case LdLeaf leaf:
                text.Append('<').Append(Describe(leaf.Node)).Append('>');
                break;
            case LdSeries series:
                text.Append("S(");
                foreach (var item in series.Items)
                {
                    Serialise(text, item);
                }

                text.Append(')');
                break;
            case LdParallel parallel:
                text.Append("P(");
                foreach (var branch in parallel.Branches)
                {
                    Serialise(text, branch);
                    text.Append('|');
                }

                text.Append(')');
                break;
        }
    }

    private static string Pins(IReadOnlyList<LdPin> pins) =>
        string.Join(',', pins.Select(p => $"{p.Name}{(p.Negated ? "!" : "")}={p.Operand}"));
}
