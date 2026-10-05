namespace Versio.Core.Iec.Ladder;

/// <summary>
/// Lays one network out the way a ladder editor draws it: the rail on the left, elements along
/// the rung, branches below, coils pushed to the right.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coils sit at the right, on every branch.</b> That is how every ladder editor draws a
/// rung, and a reviewer scans the outputs down that column. So when a rung ends, the spare width
/// goes in front of its last coil, never after it. A branch that rejoins the rung is instead
/// padded at its end with wire, because its elements belong next to where the branch opens.
/// </para>
/// <para>
/// <b>Text is measured, then cut.</b> An operand longer than its element's width is truncated
/// with an ellipsis, and the full name stays on the primitive for the hover title. An element
/// grows to fit its operand up to a limit first: a 60-character tag would otherwise either
/// stretch one contact across the pane or run over its neighbours, and both read as wrong.
/// </para>
/// <para>
/// <b>Deterministic.</b> The same network always gives the same drawing, to the hundredth of a
/// pixel, which is what lets approved drawings be compared as text.
/// </para>
/// </remarks>
public static partial class LadderLayout
{
    /// <summary>The font size every label is drawn at, in pixels.</summary>
    public const double FontSize = 12;

    /// <summary>The advance of one character of the monospace font at <see cref="FontSize"/>.</summary>
    public const double CharWidth = 7.25;

    private const double Cell = 88;
    private const double MaxCell = 200;
    private const double ElementHeight = 56;
    private const double ElementAnchor = 36;
    private const double PinRow = 20;
    private const double BoxMinWidth = 80;
    private const double BoxMaxWidth = 260;
    private const double LabelMax = 220;
    private const double Lead = 14;
    private const double BranchGap = 8;
    private const double Margin = 6;
    private const double RailX = 4;
    private const double MinRungWidth = 360;

    public static double TextWidth(string text) => (text ?? "").Length * CharWidth;

    /// <summary>The text, cut with an ellipsis to fit <paramref name="width"/>.</summary>
    public static string Fit(string text, double width)
    {
        text ??= "";
        var max = (int)Math.Floor(width / CharWidth);
        if (text.Length <= max)
        {
            return text;
        }

        return max <= 1 ? "…" : string.Concat(text.AsSpan(0, max - 1), "…");
    }

    public static LadderDrawing Lay(LadderNetwork network, LadderStyle style)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(style);

        if (network.UnsupportedLanguage is { } language)
        {
            var message = $"{language} network: not drawn here; the Raw tab has it.";
            var w = TextWidth(message) + 24;
            var rect = new LdRect(RailX, Margin, w, 32);
            return new LadderDrawing(
                rect.Right + RailX,
                rect.Bottom + Margin,
                [new LdGroup("", "placeholder", rect,
                [
                    new LdFrame(rect, Dashed: true),
                    new LdText(rect.X + 12, rect.Y + 20, message, message, LdAlign.Start, "placeholder"),
                ])],
                null);
        }

        if (network.Language == LdLanguage.FunctionBlock)
        {
            return LayFunctionBlock(network);
        }

        var shape = LadderStructure.Of(network);
        var context = new Context(
            new HashSet<LdPinRef>(network.Nets.SelectMany(n => n.Ends)),
            style.RightRailDrawn);

        if (shape.Elements.Count == 0)
        {
            return Finish(context, MinRungWidth, 32, style, null);
        }

        if (shape.Root is { } root)
        {
            var piece = Measure(root, context);
            var width = Math.Max(piece.W, MinRungWidth);
            Place(piece, RailX, Margin, width, terminal: true, context);
            return Finish(context, width, piece.H, style, null);
        }

        return Fallback(network, shape, context, style);
    }

    /// <summary>
    /// A merged network (<see cref="LadderMerge"/>): both versions on one rung, each element's
    /// "was" line under it where it was modified.
    /// </summary>
    public static LadderDrawing LayMerged(LadderMergedNetwork merged, LadderStyle style)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(style);

        var context = new Context(merged.Wired, style.RightRailDrawn, merged.Was);
        if (!LadderStructure.Walk(merged.Root).Any())
        {
            return Finish(context, MinRungWidth, 32, style, null);
        }

        var piece = Measure(merged.Root, context);
        var width = Math.Max(piece.W, MinRungWidth);
        Place(piece, RailX, Margin, width, terminal: true, context);
        return Finish(context, width, piece.H, style, null);
    }

    private sealed class Context(ISet<LdPinRef> wired, bool rightRail, IReadOnlyDictionary<string, string>? was = null)
    {
        public ISet<LdPinRef> Wired { get; } = wired;

        /// <summary>What a modified element was, by id. Empty outside a merged drawing.</summary>
        public IReadOnlyDictionary<string, string> Was { get; } = was ?? new Dictionary<string, string>();

        public bool RightRail { get; } = rightRail;

        public List<LdGroup> Groups { get; } = [];

        public List<LdPrimitive> Wires { get; } = [];

        public Dictionary<LdPinRef, (double X, double Y)> Pins { get; } = [];
    }

    private static LadderDrawing Finish(Context context, double contentWidth, double contentHeight, LadderStyle style, string? fallback)
    {
        var height = contentHeight + (2 * Margin);
        var width = RailX + contentWidth + RailX;
        var groups = new List<LdGroup>
        {
            new("", "rail", new LdRect(RailX, 0, 0, height), [new LdLine(RailX, 0, RailX, height)]),
        };

        if (style.RightRailDrawn)
        {
            var x = RailX + contentWidth;
            groups.Add(new LdGroup("", "rail", new LdRect(x, 0, 0, height), [new LdLine(x, 0, x, height)]));
        }

        if (context.Wires.Count > 0)
        {
            groups.Add(new LdGroup("", "wire", BoundsOf(context.Wires), context.Wires.ToList()));
        }

        groups.AddRange(context.Groups);
        return new LadderDrawing(width, height, groups, fallback);
    }

    private static LdRect BoundsOf(IEnumerable<LdPrimitive> items)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        foreach (var item in items)
        {
            if (item is LdLine l)
            {
                x1 = Math.Min(x1, Math.Min(l.X1, l.X2));
                x2 = Math.Max(x2, Math.Max(l.X1, l.X2));
                y1 = Math.Min(y1, Math.Min(l.Y1, l.Y2));
                y2 = Math.Max(y2, Math.Max(l.Y1, l.Y2));
            }
        }

        return x1 == double.MaxValue ? default : new LdRect(x1, y1, x2 - x1, y2 - y1);
    }

    // ---- Series / parallel ------------------------------------------------------------------

    private abstract record Piece(double W, double H, double A);

    private sealed record LeafPiece(LdNode Node, double W, double H, double A) : Piece(W, H, A);

    private sealed record SeriesPiece(IReadOnlyList<Piece> Items, double W, double H, double A) : Piece(W, H, A);

    private sealed record ParallelPiece(IReadOnlyList<Piece> Branches, double W, double H, double A) : Piece(W, H, A);

    private static Piece Measure(LdBlock block, Context context)
    {
        switch (block)
        {
            case LdLeaf leaf:
                var drawn = Draw(leaf.Node, 0, 0, context);
                return new LeafPiece(leaf.Node, drawn.Group.Bounds.W, drawn.Group.Bounds.H, drawn.Anchor);

            case LdSeries series:
            {
                var items = series.Items.Select(i => Measure(i, context)).ToList();
                var a = items.Max(i => i.A);
                return new SeriesPiece(items, items.Sum(i => i.W), items.Max(i => a - i.A + i.H), a);
            }

            case LdParallel parallel:
            {
                var branches = parallel.Branches.Select(b => Measure(b, context)).ToList();
                return new ParallelPiece(
                    branches,
                    branches.Max(b => b.W),
                    branches.Sum(b => b.H) + (BranchGap * (branches.Count - 1)),
                    branches[0].A);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(block), block.GetType().Name, "unknown ladder block");
        }
    }

    private static void Place(Piece piece, double x, double top, double width, bool terminal, Context context)
    {
        switch (piece)
        {
            case LeafPiece leaf when width <= leaf.W + 0.01:
                var group = Draw(leaf.Node, x, top, context).Group;

                // The last coil of a rung with no right rail ends at its bracket: a lead running
                // on to the edge reads as a rung that continues into nothing.
                if (terminal && !context.RightRail && IsOutput(leaf))
                {
                    var right = group.Bounds.Right;
                    group = group with
                    {
                        Items = group.Items
                            .Where(i => i is not LdLine l || Math.Abs(l.X2 - right) > 0.01 || l.Y1 != l.Y2)
                            .ToList(),
                    };
                }

                context.Groups.Add(group);
                break;
            case LeafPiece leaf:
                PlaceSeries([leaf], leaf.A, x, top, width, terminal, context);
                break;
            case SeriesPiece series:
                PlaceSeries(series.Items, series.A, x, top, width, terminal, context);
                break;
            case ParallelPiece parallel:
                PlaceParallel(parallel, x, top, width, terminal, context);
                break;
        }
    }

    private static bool IsOutput(Piece piece) =>
        piece is LeafPiece { Node: LdCoil or LdJump or LdReturn };

    private static void PlaceSeries(
        IReadOnlyList<Piece> items, double a, double x, double top, double width, bool terminal, Context context)
    {
        var extra = Math.Max(0, width - items.Sum(i => i.W));
        var last = items.Count - 1;

        // At the end of a rung the spare width goes in front of the coil, so every output
        // lines up on the right. A branch block there is stretched instead, so the coils at
        // the end of each of its branches line up too.
        var gapBefore = terminal && IsOutput(items[last]);
        var stretchLast = terminal && items[last] is ParallelPiece;
        var y = top + a;

        var cx = x;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var w = item.W;
            if (i == last && gapBefore && extra > 0)
            {
                context.Wires.Add(new LdLine(cx, y, cx + extra, y));
                cx += extra;
            }

            if (i == last && stretchLast)
            {
                w += extra;
            }

            Place(item, cx, top + a - item.A, w, terminal && i == last, context);
            cx += w;
        }

        // A rung that ends in a box with nothing after it stops at the box; with no right rail
        // a wire running on into empty space would read as an open contact.
        if (extra > 0 && !gapBefore && !stretchLast && (!terminal || context.RightRail))
        {
            context.Wires.Add(new LdLine(cx, y, x + width, y));
        }
    }

    private static void PlaceParallel(ParallelPiece parallel, double x, double top, double width, bool terminal, Context context)
    {
        var branchTop = top;
        var first = top + parallel.A;
        var lastAnchor = first;
        foreach (var branch in parallel.Branches)
        {
            Place(branch, x, branchTop, width, terminal, context);
            lastAnchor = branchTop + branch.A;
            branchTop += branch.H + BranchGap;
        }

        context.Wires.Add(new LdLine(x, first, x, lastAnchor));
        if (!terminal || context.RightRail)
        {
            context.Wires.Add(new LdLine(x + width, first, x + width, lastAnchor));
        }
    }

    // ---- Fallback: rank placement ----------------------------------------------------------

    /// <summary>
    /// Places a network that has no series/parallel shape: one column per step of power flow
    /// from the rail, with every wire routed square.
    /// </summary>
    /// <remarks>
    /// It looks less like the IDE, but every element and every wire of the file is drawn. The
    /// alternative, forcing the network into a tree, would draw connections that do not exist.
    /// </remarks>
    private static LadderDrawing Fallback(LadderNetwork network, LadderShape shape, Context context, LadderStyle style)
    {
        var producers = new Dictionary<int, List<string>>();
        var netOf = new Dictionary<LdPinRef, int>();
        for (var i = 0; i < network.Nets.Count; i++)
        {
            foreach (var end in network.Nets[i].Ends)
            {
                netOf[end] = i;
            }
        }

        foreach (var node in network.Nodes)
        {
            foreach (var pin in LadderStructure.PowerPins(node, context.Wired).Out)
            {
                var net = netOf[new LdPinRef(node.Id, pin)];
                if (!producers.TryGetValue(net, out var list))
                {
                    producers[net] = list = [];
                }

                list.Add(node.Id);
            }
        }

        var rank = shape.Elements.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        var rails = network.Nodes.OfType<LdLeftRail>().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        for (var pass = 0; pass < shape.Elements.Count; pass++)
        {
            foreach (var node in shape.Elements)
            {
                foreach (var pin in LadderStructure.PowerPins(node, context.Wired).In)
                {
                    if (!producers.TryGetValue(netOf[new LdPinRef(node.Id, pin)], out var from))
                    {
                        continue;
                    }

                    foreach (var p in from.Where(p => !rails.Contains(p) && rank.ContainsKey(p)))
                    {
                        rank[node.Id] = Math.Max(rank[node.Id], Math.Min(rank[p] + 1, shape.Elements.Count));
                    }
                }
            }
        }

        var x = RailX + 20;
        var bottom = Margin;
        var right = x;
        foreach (var column in shape.Elements.GroupBy(n => rank[n.Id]).OrderBy(g => g.Key))
        {
            var y = Margin;
            var columnWidth = 0.0;
            foreach (var node in column)
            {
                var drawn = Draw(node, x, y, context);
                context.Groups.Add(drawn.Group);
                foreach (var (pin, point) in drawn.Pins)
                {
                    context.Pins[new LdPinRef(node.Id, pin)] = point;
                }

                columnWidth = Math.Max(columnWidth, drawn.Group.Bounds.W);
                y = drawn.Group.Bounds.Bottom + 12;
                bottom = Math.Max(bottom, drawn.Group.Bounds.Bottom);
            }

            x += columnWidth + 28;
            right = x;
        }

        foreach (var net in network.Nets)
        {
            var sinks = net.Ends.Where(e => context.Pins.ContainsKey(e) && IsInput(e)).ToList();
            foreach (var source in net.Ends.Where(e => !sinks.Contains(e)))
            {
                foreach (var sink in sinks)
                {
                    var t = context.Pins[sink];
                    if (rails.Contains(source.NodeId))
                    {
                        context.Wires.Add(new LdLine(RailX, t.Y, t.X, t.Y));
                        continue;
                    }

                    if (!context.Pins.TryGetValue(source, out var s))
                    {
                        continue;
                    }

                    var bend = t.X - 10 > s.X ? t.X - 10 : s.X + 10;
                    context.Wires.Add(new LdLine(s.X, s.Y, bend, s.Y));
                    context.Wires.Add(new LdLine(bend, s.Y, bend, t.Y));
                    context.Wires.Add(new LdLine(bend, t.Y, t.X, t.Y));
                }
            }
        }

        return Finish(context, Math.Max(right - RailX, MinRungWidth), bottom - Margin, style, shape.FallbackReason);

        bool IsInput(LdPinRef end)
        {
            var node = network.Nodes.FirstOrDefault(n => n.Id == end.NodeId);
            return node is not null && LadderStructure.PowerPins(node, context.Wired).In.Contains(end.Pin);
        }
    }

    // ---- One element ------------------------------------------------------------------------

    private sealed record Drawn(LdGroup Group, double Anchor, IReadOnlyDictionary<string, (double X, double Y)> Pins);

    private static Drawn Draw(LdNode node, double x, double top, Context context)
    {
        var drawn = Draw(node, x, top, context.Wired);
        return context.Was.TryGetValue(node.Id, out var was) ? WithWas(drawn, was) : drawn;
    }

    private static Drawn Draw(LdNode node, double x, double top, ISet<LdPinRef> wired) => node switch
    {
        LdContact c => TwoTerminal(c, "contact", c.Operand, c.EdgeMemory, x, top, ContactSymbol(c.Kind)),
        LdCoil c => TwoTerminal(c, "coil", c.Operand, c.EdgeMemory, x, top, CoilSymbol(c.Kind)),
        LdJump j => TwoTerminal(j, "jump", j.Target, null, x, top, Tag(j.IfNot ? "JMPN" : "JMP")),
        LdReturn r => TwoTerminal(r, "return", "", null, x, top, Tag("RET")),
        LdBox { InlineCompare: true } b => Compare(b, x, top, wired),
        LdBox b => Box(b, "box", b.TypeName, b.Detail, b.Instance, b.Pins, dashed: false, x, top, wired),
        LdUnsupported u => Box(u, "unsupported", u.VendorName, "unrecognised", null, u.Pins, dashed: true, x, top, wired),
        _ => TwoTerminal(node, "unsupported", node.GetType().Name, null, x, top, Tag("?")),
    };

    /// <summary>
    /// A modified element with what it was written underneath: one more line, inside the
    /// element, so the invariants still hold and nothing below it moves into it.
    /// </summary>
    /// <remarks>
    /// Under rather than instead: the merged view shows the element as it is now (that is what
    /// runs), and the old value is a note on it. Cut to the element's width like any label; the
    /// full text stays on the hover.
    /// </remarks>
    private static Drawn WithWas(Drawn drawn, string was)
    {
        var b = drawn.Group.Bounds;
        var bounds = b with { H = b.H + 16 };
        var text = new LdText(b.X + (b.W / 2), b.Bottom + 10, Fit(was, b.W - 8), was, LdAlign.Middle, "was");
        return drawn with { Group = drawn.Group with { Bounds = bounds, Items = [.. drawn.Group.Items, text] } };
    }

    /// <summary>Draws a symbol centred on (cx, cy) and returns its primitives and half-width.</summary>
    private delegate (IReadOnlyList<LdPrimitive> Items, double Half) Symbol(double cx, double cy);

    private static Drawn TwoTerminal(
        LdNode node, string kind, string operand, string? memory, double x, double top, Symbol symbol)
    {
        var w = Math.Clamp(TextWidth(operand) + 16, Cell, MaxCell);
        var h = ElementHeight + (memory is null ? 0 : 16);
        var cx = x + (w / 2);
        var cy = top + ElementAnchor;
        var (shape, half) = symbol(cx, cy);

        var items = new List<LdPrimitive>
        {
            new LdLine(x, cy, cx - half, cy),
        };

        // Outputs end the rung, so they have nothing to their right unless something follows.
        items.Add(new LdLine(cx + half, cy, x + w, cy));
        items.AddRange(shape);
        if (operand.Length > 0)
        {
            items.Add(new LdText(cx, top + 16, Fit(operand, w - 8), operand, LdAlign.Middle, "operand"));
        }

        if (memory is not null)
        {
            items.Add(new LdText(cx, cy + 30, Fit(memory, w - 8), memory, LdAlign.Middle, "operand"));
        }

        return new Drawn(
            new LdGroup(node.Id, kind, new LdRect(x, top, w, h), items),
            ElementAnchor,
            new Dictionary<string, (double, double)>(StringComparer.Ordinal)
            {
                [LdPins.In] = (x, cy),
                [LdPins.Out] = (x + w, cy),
            });
    }

    private static Symbol ContactSymbol(LdContactKind kind) => (cx, cy) =>
    {
        var items = new List<LdPrimitive>
        {
            new LdLine(cx - 10, cy - 11, cx - 10, cy + 11),
            new LdLine(cx + 10, cy - 11, cx + 10, cy + 11),
        };
        switch (kind)
        {
            case LdContactKind.NormallyClosed:
                items.Add(new LdLine(cx - 7, cy + 9, cx + 7, cy - 9));
                break;
            case LdContactKind.PositiveEdge:
                items.Add(new LdText(cx, cy + 4, "P", "P", LdAlign.Middle, "symbol"));
                break;
            case LdContactKind.NegativeEdge:
                items.Add(new LdText(cx, cy + 4, "N", "N", LdAlign.Middle, "symbol"));
                break;
        }

        return (items, 10);
    };

    private static Symbol CoilSymbol(LdCoilKind kind) => (cx, cy) =>
    {
        var items = new List<LdPrimitive>
        {
            new LdPath(FormattableString.Invariant($"M {cx - 6} {cy - 11} Q {cx - 13} {cy} {cx - 6} {cy + 11}")),
            new LdPath(FormattableString.Invariant($"M {cx + 6} {cy - 11} Q {cx + 13} {cy} {cx + 6} {cy + 11}")),
        };
        var letter = kind switch
        {
            LdCoilKind.Set => "S",
            LdCoilKind.Reset => "R",
            LdCoilKind.PositiveEdge => "P",
            LdCoilKind.NegativeEdge => "N",
            _ => null,
        };
        if (letter is not null)
        {
            items.Add(new LdText(cx, cy + 4, letter, letter, LdAlign.Middle, "symbol"));
        }

        if (kind == LdCoilKind.Negated)
        {
            items.Add(new LdLine(cx - 5, cy + 8, cx + 5, cy - 8));
        }

        return (items, 10);
    };

    private static Symbol Tag(string text) => (cx, cy) =>
    {
        var w = TextWidth(text) + 10;
        var rect = new LdRect(cx - (w / 2), cy - 10, w, 20);
        return ([new LdFrame(rect), new LdText(cx, cy + 4, text, text, LdAlign.Middle, "symbol")], w / 2);
    };

    /// <summary>A comparator drawn in line: operand above, the comparison in a small box, operand below.</summary>
    private static Drawn Compare(LdBox box, double x, double top, ISet<LdPinRef> wired)
    {
        var operands = box.Pins.Where(p => p.Operand is not null).Select(p => p.Operand!).ToList();
        var above = operands.ElementAtOrDefault(0) ?? "";
        var below = operands.ElementAtOrDefault(1) ?? "";
        var w = Math.Clamp(Math.Max(TextWidth(above), TextWidth(below)) + 16, Cell, MaxCell);
        var cx = x + (w / 2);
        const double anchor = 40;
        var cy = top + anchor;
        var symbolWidth = Math.Max(36, Math.Max(TextWidth(box.TypeName), TextWidth(box.Detail ?? "")) + 10);
        var rect = new LdRect(cx - (symbolWidth / 2), cy - 15, symbolWidth, 30);

        var items = new List<LdPrimitive>
        {
            new LdLine(x, cy, rect.X, cy),
            new LdLine(rect.Right, cy, x + w, cy),
            new LdFrame(rect),
            new LdText(cx, cy - 2, box.TypeName, box.TypeName, LdAlign.Middle, "type"),
            new LdText(cx, top + 16, Fit(above, w - 8), above, LdAlign.Middle, "operand"),
            new LdText(cx, cy + 34, Fit(below, w - 8), below, LdAlign.Middle, "operand"),
        };
        if (box.Detail is { Length: > 0 } detail)
        {
            items.Add(new LdText(cx, cy + 11, detail, detail, LdAlign.Middle, "detail"));
        }

        var (ins, outs) = LadderStructure.PowerPins(box, wired);
        var pins = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var pin in ins)
        {
            pins[pin] = (x, cy);
        }

        foreach (var pin in outs)
        {
            pins[pin] = (x + w, cy);
        }

        return new Drawn(new LdGroup(box.Id, "compare", new LdRect(x, top, w, 84), items), anchor, pins);
    }

    /// <summary>
    /// A box: type name on top, inputs down the left, outputs down the right. Operands are labels
    /// outside the frame; power-flow pins are wires out to the element's edge.
    /// </summary>
    private static Drawn Box(
        LdNode node, string kind, string title, string? detail, string? instance,
        IReadOnlyList<LdPin> allPins, bool dashed, double x, double top, ISet<LdPinRef> wired,
        bool keepOrder = false, IReadOnlyList<double>? leftRows = null)
    {
        var (powerIn, powerOut) = LadderStructure.PowerPins(node, wired);

        // Power flow enters on the first row and leaves on the first row, so the rung runs
        // straight through the box: OrderBy is stable, so the rest keep the vendor's order.
        // Not in a function block diagram: there every input is wired or labelled, and in1
        // drawn below in2 because in2 happened to be the wired one reads as a different box.
        var left = allPins.Where(p => p.Direction != LdPinDirection.Output)
            .OrderBy(p => keepOrder || powerIn.Contains(p.Name) ? 0 : 1).ToList();
        var right = allPins.Where(p => p.Direction == LdPinDirection.Output)
            .OrderBy(p => keepOrder || powerOut.Contains(p.Name) ? 0 : 1).ToList();

        double LabelWidth(IEnumerable<LdPin> pins) => Math.Clamp(
            pins.Where(p => p.Operand is not null).Select(p => TextWidth(p.Operand!) + 16).DefaultIfEmpty(0).Max(),
            Lead,
            LabelMax);

        var leftLabel = LabelWidth(left);
        var rightLabel = LabelWidth(right);
        var leftNames = left.Select(p => TextWidth(p.Name)).DefaultIfEmpty(0).Max();
        var rightNames = right.Select(p => TextWidth(p.Name)).DefaultIfEmpty(0).Max();
        var names = leftNames + rightNames + 24;
        var boxWidth = Math.Min(
            BoxMaxWidth,
            Math.Max(Math.Max(BoxMinWidth, names), Math.Max(TextWidth(title), TextWidth(detail ?? "")) + 16));

        var instanceHeight = instance is null ? 0 : 18;
        var header = detail is { Length: > 0 } ? 34 : 22;
        var rows = Math.Max(1, Math.Max(left.Count, right.Count));
        var boxTop = top + 4 + instanceHeight;

        // Left rows at fixed spacing, unless the caller placed them: a function block diagram
        // lines each input up with the output feeding it, and the box stretches to fit (see
        // LayFunctionBlock). Rows given are centres relative to the element's top.
        double LeftRow(int i) => leftRows is { } given && i < given.Count
            ? top + given[i]
            : boxTop + header + (i * PinRow) + (PinRow / 2);
        var boxHeight = Math.Max(
            header + (rows * PinRow) + 4,
            left.Count == 0 ? 0 : LeftRow(left.Count - 1) + (PinRow / 2) + 4 - boxTop);
        var w = leftLabel + boxWidth + rightLabel;
        var h = 4 + instanceHeight + boxHeight + 6;
        var boxX = x + leftLabel;
        var cx = boxX + (boxWidth / 2);

        var items = new List<LdPrimitive> { new LdFrame(new LdRect(boxX, boxTop, boxWidth, boxHeight), dashed) };
        if (instance is not null)
        {
            // Centred on the frame, which need not be the element's centre, so it may only use
            // the narrower of the two sides.
            var room = 2 * Math.Min(cx - x, x + w - cx);
            items.Add(new LdText(cx, boxTop - 5, Fit(instance, room - 4), instance, LdAlign.Middle, "instance"));
        }

        items.Add(new LdText(cx, boxTop + 15, Fit(title, boxWidth - 8), title, LdAlign.Middle, "type"));
        if (detail is { Length: > 0 })
        {
            items.Add(new LdText(cx, boxTop + 29, Fit(detail, boxWidth - 8), detail, LdAlign.Middle, "detail"));
        }

        // Each side's pin names get what the other side leaves, not half each: "Cmd" beside
        // "Running" must not cut "Running" to "Runn…" while the left half stands empty (seen in
        // the first specimen gallery). Only a box at its width cap falls back to halves.
        var fits = names <= boxWidth;
        var leftRoom = fits ? boxWidth - rightNames - 12 : (boxWidth / 2) - 6;
        var rightRoom = fits ? boxWidth - leftNames - 12 : (boxWidth / 2) - 6;

        var pins = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        for (var i = 0; i < left.Count; i++)
        {
            var pin = left[i];
            var y = LeftRow(i);
            var edge = boxX - (pin.Negated ? 6 : 0);
            items.Add(new LdText(boxX + 4, y + 4, Fit(pin.Name, leftRoom), pin.Name, LdAlign.Start, "pin"));
            if (pin.Negated)
            {
                items.Add(new LdCircle(boxX - 3, y, 3));
            }

            if (powerIn.Contains(pin.Name))
            {
                items.Add(new LdLine(x, y, edge, y));
                pins[pin.Name] = (x, y);
            }
            else if (pin.Operand is { } operand)
            {
                items.Add(new LdLine(edge - 4, y, edge, y));
                items.Add(new LdText(edge - 6, y + 4, Fit(operand, leftLabel - 16), operand, LdAlign.End, "operand"));
            }
        }

        for (var i = 0; i < right.Count; i++)
        {
            var pin = right[i];
            var y = boxTop + header + (i * PinRow) + (PinRow / 2);
            var edge = boxX + boxWidth + (pin.Negated ? 6 : 0);
            items.Add(new LdText(boxX + boxWidth - 4, y + 4, Fit(pin.Name, rightRoom), pin.Name, LdAlign.End, "pin"));
            if (pin.Negated)
            {
                items.Add(new LdCircle(boxX + boxWidth + 3, y, 3));
            }

            if (powerOut.Contains(pin.Name))
            {
                items.Add(new LdLine(edge, y, x + w, y));
                pins[pin.Name] = (x + w, y);
            }
            else if (pin.Operand is { } operand)
            {
                items.Add(new LdLine(edge, y, edge + 4, y));
                items.Add(new LdText(edge + 6, y + 4, Fit(operand, rightLabel - 16), operand, LdAlign.Start, "operand"));
            }
        }

        var anchor = 4 + instanceHeight + header + (PinRow / 2);
        return new Drawn(new LdGroup(node.Id, kind, new LdRect(x, top, w, h), items), anchor, pins);
    }
}
