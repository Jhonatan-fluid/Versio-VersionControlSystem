namespace Versio.Core.Iec.Ladder;

/// <summary>
/// The function block diagram half of the layout: boxes wired left to right into the outputs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Columns by distance from the inputs.</b> An FBD network is a forest: its outputs (an
/// assignment, a call, a box nothing reads) are roots, and each box feeds the box to its right.
/// Every box goes in the column of its longest path from a box that reads no other box, so a box
/// always stands right of everything that feeds it, and every chain starts at the left edge.
/// Counting from the outputs instead was the first version, and it pushed a lone call (one
/// that feeds only an operand) to the far right of a network with a long chain elsewhere:
/// <c>BoolToDigVal</c> in the fixture's <c>P100</c> stood in the last column with nothing
/// before it. TIA starts every chain at the left.
/// </para>
/// <para>
/// <b>Rows by tree order.</b> The roots are placed in the vendor's order, top to bottom, each
/// after its inputs: a box's inputs are stacked from its top, in pin order, and the box then
/// lines its first wired input up with the output feeding it where it can. Every box gets rows
/// nothing placed earlier uses, so no two boxes overlap. A box in a column further right can share
/// rows with its inputs, which is the point.
/// </para>
/// <para>
/// <b>An output read twice</b> (fan-out, which TIA FBD allows and the CleaningStation fixture
/// never uses) is placed once, under its first reader, and the second reader gets a square wire
/// to it. Drawing it twice would show two boxes where the PLC runs one.
/// </para>
/// </remarks>
public static partial class LadderLayout
{
    private const double FbdColumnGap = 36;
    private const double FbdRowGap = 14;
    private const double FbdLeft = 8;

    private static LadderDrawing LayFunctionBlock(LadderNetwork network, IReadOnlyDictionary<string, string>? was)
    {
        // A modified element of a merged view carries its "was" line, inside its own bounds,
        // so every measurement below already includes it.
        Drawn Element(LdNode node, double x, double top, ISet<LdPinRef> pinsWired, IReadOnlyList<double>? leftRows = null)
        {
            var drawn = DrawFunctionBlock(node, x, top, pinsWired, leftRows);
            return was is not null && was.TryGetValue(node.Id, out var text) ? WithWas(drawn, text) : drawn;
        }

        var wired = new HashSet<LdPinRef>(network.Nets.SelectMany(n => n.Ends));
        var nodes = network.Nodes.Where(n => n is not (LdLeftRail or LdLabel)).ToList();
        if (nodes.Count == 0)
        {
            return new LadderDrawing(MinRungWidth, 32 + (2 * Margin), [], null);
        }

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        // Which output feeds each input, and who reads each box. A net with no output on it,
        // or two, connects nothing a reader can follow; its pins stay unwired in the drawing.
        var feeds = new Dictionary<LdPinRef, LdPinRef>();
        var readers = nodes.ToDictionary(n => n.Id, _ => new List<LdPinRef>(), StringComparer.Ordinal);
        foreach (var net in network.Nets)
        {
            var ends = net.Ends.Where(e => byId.ContainsKey(e.NodeId)).ToList();
            var outputs = ends.Where(e => FbdOutputs(byId[e.NodeId]).Contains(e.Pin)).ToList();
            if (outputs.Count != 1)
            {
                continue;
            }

            foreach (var input in ends.Where(e => e != outputs[0]))
            {
                feeds[input] = outputs[0];
                readers[outputs[0].NodeId].Add(input);
            }
        }

        // Longest path from an input. The guard stops a cycle (which a PLC rejects) from
        // recursing for ever; such a network is still drawn, just not well.
        var level = new Dictionary<string, int>(StringComparer.Ordinal);
        int Level(LdNode node, int guard)
        {
            if (level.TryGetValue(node.Id, out var known))
            {
                return known;
            }

            var producers = FbdInputs(node)
                .Select(p => new LdPinRef(node.Id, p))
                .Where(feeds.ContainsKey)
                .Select(p => byId[feeds[p].NodeId])
                .ToList();
            var l = guard > nodes.Count || producers.Count == 0
                ? 0
                : 1 + producers.Max(p => Level(p, guard + 1));
            level[node.Id] = l;
            return l;
        }

        foreach (var node in nodes)
        {
            Level(node, 0);
        }

        var deepest = level.Values.Max();
        int Column(string id) => level[id];

        var measured = nodes.ToDictionary(n => n.Id, n => Element(n, 0, 0, wired), StringComparer.Ordinal);
        var widths = new double[deepest + 1];
        foreach (var node in nodes)
        {
            widths[Column(node.Id)] = Math.Max(widths[Column(node.Id)], measured[node.Id].Group.Bounds.W);
        }

        var columnX = new double[deepest + 1];
        for (var c = 0; c <= deepest; c++)
        {
            columnX[c] = c == 0 ? FbdLeft : columnX[c - 1] + widths[c - 1] + FbdColumnGap;
        }

        var placed = new Dictionary<string, double>(StringComparer.Ordinal);
        var rows = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        var drawnAt = new Dictionary<string, Drawn>(StringComparer.Ordinal);
        var cursor = Margin;

        void Place(LdNode node)
        {
            if (placed.ContainsKey(node.Id))
            {
                return;
            }

            // Reserved first, so a cycle back to this box stops here.
            placed[node.Id] = double.NaN;
            var top = cursor;
            var inputs = FbdInputs(node).Select(p => new LdPinRef(node.Id, p)).Where(feeds.ContainsKey).ToList();
            foreach (var input in inputs)
            {
                Place(byId[feeds[input].NodeId]);
            }

            double? FedAt(LdPinRef input) =>
                feeds.TryGetValue(input, out var producer) && !double.IsNaN(placed[producer.NodeId])
                    ? placed[producer.NodeId] + drawnAt[producer.NodeId].Pins[producer.Pin].Y
                    : null;

            var y = top;
            if (node is LdBox or LdUnsupported)
            {
                // Each input row level with the output feeding it, so every wire into a gate is
                // one straight line. At fixed spacing, seven comparators into one OR bent at the
                // same x and their verticals merged into what read as a bus (the fixture's
                // Service block, 2026-10-05). Rows never come closer than one pin apart; the box
                // stretches instead.
                var all = FbdInputs(node).Select(p => new LdPinRef(node.Id, p)).ToList();
                var row0 = FirstRow(node);
                var k = all.FindIndex(i => FedAt(i) is not null);
                if (k >= 0)
                {
                    y = Math.Max(top, FedAt(all[k])!.Value - (row0 + (k * PinRow)));
                }

                var centres = new List<double>();
                foreach (var input in all)
                {
                    var next = centres.Count == 0 ? y + row0 : centres[^1] + PinRow;
                    centres.Add(Math.Max(next, FedAt(input) ?? double.MinValue));
                }

                rows[node.Id] = centres.Select(c => c - y).ToList();
            }
            else
            {
                var first = inputs.FirstOrDefault(i => FedAt(i) is not null);
                if (first != default)
                {
                    y = Math.Max(top, FedAt(first)!.Value - measured[node.Id].Pins[first.Pin].Y);
                }
            }

            placed[node.Id] = y;
            drawnAt[node.Id] = Element(node, 0, 0, wired, rows.GetValueOrDefault(node.Id));
            cursor = Math.Max(cursor, y + drawnAt[node.Id].Group.Bounds.H + FbdRowGap);
        }

        foreach (var root in nodes.Where(n => readers[n.Id].Count == 0))
        {
            Place(root);
        }

        foreach (var node in nodes)
        {
            Place(node);
        }

        var groups = new List<LdGroup>();
        var pins = new Dictionary<LdPinRef, (double X, double Y)>();
        foreach (var node in nodes)
        {
            var drawn = Element(node, columnX[Column(node.Id)], placed[node.Id], wired, rows.GetValueOrDefault(node.Id));
            groups.Add(drawn.Group);
            foreach (var (pin, point) in drawn.Pins)
            {
                pins[new LdPinRef(node.Id, pin)] = point;
            }
        }

        var wires = new List<LdPrimitive>();
        foreach (var (input, output) in feeds)
        {
            if (!pins.TryGetValue(input, out var to) || !pins.TryGetValue(output, out var from))
            {
                continue;
            }

            if (Math.Abs(from.Y - to.Y) < 0.01)
            {
                wires.Add(new LdLine(from.X, from.Y, to.X, to.Y));
                continue;
            }

            // Square, bending in the gap just left of the reader, so a wire never crosses a box.
            var bend = to.X > from.X ? to.X - (FbdColumnGap / 2) : from.X + (FbdColumnGap / 2);
            wires.Add(new LdLine(from.X, from.Y, bend, from.Y));
            wires.Add(new LdLine(bend, from.Y, bend, to.Y));
            wires.Add(new LdLine(bend, to.Y, to.X, to.Y));
        }

        if (wires.Count > 0)
        {
            groups.Insert(0, new LdGroup("", "wire", BoundsOf(wires), wires));
        }

        var right = groups.Max(g => g.Bounds.Right);
        var bottom = groups.Max(g => g.Bounds.Bottom);
        return new LadderDrawing(Math.Max(right + FbdLeft, MinRungWidth), bottom + Margin, groups, null);
    }

    /// <summary>The pins a node of a function block diagram gives its result on.</summary>
    private static IEnumerable<string> FbdOutputs(LdNode node) => node switch
    {
        LdBox b => b.Pins.Where(p => p.Direction == LdPinDirection.Output).Select(p => p.Name),
        LdUnsupported u => u.Pins.Where(p => p.Direction == LdPinDirection.Output).Select(p => p.Name),
        _ => [LdPins.Out],
    };

    /// <summary>The pins a node of a function block diagram reads, top to bottom.</summary>
    private static IEnumerable<string> FbdInputs(LdNode node) => node switch
    {
        LdBox b => b.Pins.Where(p => p.Direction != LdPinDirection.Output).Select(p => p.Name),
        LdUnsupported u => u.Pins.Where(p => p.Direction != LdPinDirection.Output).Select(p => p.Name),
        _ => [LdPins.In],
    };

    private static Drawn DrawFunctionBlock(
        LdNode node, double x, double top, ISet<LdPinRef> wired, IReadOnlyList<double>? rows = null) => node switch
    {
        LdCoil c => Assignment(c, x, top, wired),
        LdBox b => Box(b, "box", b.TypeName, b.Detail, b.Instance, b.Pins, dashed: false, x, top, wired, keepOrder: true, rows),
        LdUnsupported u => Box(u, "unsupported", u.VendorName, "unrecognised", null, u.Pins, dashed: true, x, top, wired, keepOrder: true, rows),
        _ => Draw(node, x, top, wired),
    };

    /// <summary>Where a box's first input row sits below its top, as <see cref="Box"/> lays it out.</summary>
    private static double FirstRow(LdNode node)
    {
        var (instance, detail) = node switch
        {
            LdBox b => (b.Instance, b.Detail),
            _ => (null, "unrecognised"),
        };

        return 4 + (instance is null ? 0 : 18) + (detail is { Length: > 0 } ? 34 : 22) + (PinRow / 2);
    }

    /// <summary>
    /// An assignment, the FBD coil: a small box with its operand above it, the IEC "=" (or S, R,
    /// an edge) inside, fed from the left.
    /// </summary>
    private static Drawn Assignment(LdCoil coil, double x, double top, ISet<LdPinRef> wired)
    {
        const double BoxWidth = 40;
        const double BoxHeight = 24;
        var w = Math.Clamp(Math.Max(Lead + BoxWidth, TextWidth(coil.Operand) + 8), Lead + BoxWidth, MaxCell);
        var boxX = x + (w / 2) - (BoxWidth / 2);
        var boxTop = top + 20;
        var cy = boxTop + (BoxHeight / 2);
        var symbol = coil.Kind switch
        {
            LdCoilKind.Set => "S",
            LdCoilKind.Reset => "R",
            LdCoilKind.PositiveEdge => "P",
            LdCoilKind.NegativeEdge => "N",
            LdCoilKind.Negated => "/=",
            _ => "=",
        };

        var items = new List<LdPrimitive>
        {
            new LdFrame(new LdRect(boxX, boxTop, BoxWidth, BoxHeight)),
            new LdText(boxX + (BoxWidth / 2), cy + 4, symbol, symbol, LdAlign.Middle, "type"),
            new LdText(x + (w / 2), top + 14, Fit(coil.Operand, w - 8), coil.Operand, LdAlign.Middle, "operand"),
        };

        var height = 20 + BoxHeight + 6;
        if (coil.EdgeMemory is { } memory)
        {
            items.Add(new LdText(x + (w / 2), boxTop + BoxHeight + 14, Fit(memory, w - 8), memory, LdAlign.Middle, "operand"));
            height += 16;
        }

        // The input lead only when something feeds it: an assignment fed by nothing is a network
        // the IDE would refuse to compile, and a lead to nowhere would hide that.
        var pins = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        if (wired.Contains(new LdPinRef(coil.Id, LdPins.In)))
        {
            items.Insert(0, new LdLine(x, cy, boxX, cy));
            pins[LdPins.In] = (x, cy);
        }
        if (wired.Contains(new LdPinRef(coil.Id, LdPins.Out)))
        {
            items.Add(new LdLine(boxX + BoxWidth, cy, x + w, cy));
            pins[LdPins.Out] = (x + w, cy);
        }

        return new Drawn(new LdGroup(coil.Id, "coil", new LdRect(x, top, w, height), items), cy - top, pins);
    }
}
