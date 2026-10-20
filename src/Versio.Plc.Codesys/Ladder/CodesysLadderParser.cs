using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Versio.Core.Iec.Ladder;
using Versio.Plc.Codesys.FileBased;

namespace Versio.Plc.Codesys.Ladder;

/// <summary>
/// A CODESYS LD or FBD body, read from the native XML into the neutral ladder model.
/// </summary>
/// <remarks>
/// <para>
/// <b>One body, three views.</b> CODESYS stores LD, FBD and IL as one "network list"
/// (Implementation <c>{25e509de}</c>) and lets the IDE show it as any of the three.
/// <c>DefaultViewMode</c> says which one the block opens in, and that is the one drawn here. A
/// network the IDE shows as IL (<c>ILActive</c>) is kept as a placeholder, never dropped.
/// </para>
/// <para>
/// <b>Two containers, one parser.</b> A classic export (<c>Signals.xml</c> in the mirror) wraps
/// the object in <c>ExportFile/StructuredView/EntryList</c>. A File-Based Storage file
/// (<c>Signals.pou.xml.v3</c>) is the same object XML after a <c>__V3_META__</c> header, with no
/// wrapper. Once the object element is found the two are read the same way, and the fixture's two
/// copies of <c>Signals</c> are tested to parse to the same networks.
/// </para>
/// <para>
/// <b>An expression tree, not a graph.</b> TIA Portal writes parts and wires; CODESYS writes, per
/// network, one assignment: an <c>RValue</c> expression of nested boxes whose leaves are
/// operands, and a list of outputs (coils, jumps). The neutral model is a graph, and this file
/// turns the tree into one:
/// <list type="bullet">
/// <item>In <b>ladder</b>, power flows from the rail into the expression. A Boolean operand is a
/// contact. <c>AND</c> puts its inputs in series and <c>OR</c> in parallel. Any other box takes
/// power on its first input (or <c>EN</c>), and that input's expression is the rung before it.
/// Its other inputs are operands written beside their pins. The outputs hang off the end.</item>
/// <item>In <b>FBD</b> there is no rail. Every box, <c>AND</c> and <c>OR</c> included, is a box.
/// An operand labels the pin it feeds, a nested box is wired to it, and the outputs are
/// assignments.</item>
/// </list>
/// <see cref="LadderStructure"/> then recovers the series/parallel shape it would recover from any
/// vendor's wires. A box whose second input is itself a box takes power on two pins and gets the
/// plainer placement, which is honest: the network has no rung shape.
/// </para>
/// <para>
/// <b>What is inferred.</b> The fixture has one block, <c>Signals</c>, with no OR, negation,
/// edge, set/reset, EN/ENO or return. Two readings come from it alone:
/// <list type="bullet">
/// <item>An output item with <c>Flags = 4</c> is a jump. Network 1's <c>DoorIsOpen</c> with
/// <c>Boolean = False</c> names network 3's label.</item>
/// <item>An <c>EN</c> box whose input list is one longer than its parameters spends the first
/// item on <c>EN</c>.</item>
/// </list>
/// Any other flag value is drawn as an unrecognised element that names it, rather than as a plain
/// contact or coil. So is an operand CODESYS does not mark <c>Boolean</c> where power flows. A diagram that shows a reset coil as a normal one is a diagram that lies. See
/// <c>docs/fbs/PLAN.md</c> §5 for the specimens that would settle each one.
/// </para>
/// </remarks>
public static class CodesysLadderParser
{
    /// <summary>How the CODESYS LD editor draws: networks numbered from 1, both power rails.</summary>
    public static LadderStyle Style { get; } = new() { UnitName = "Network", RightRailDrawn = true };

    private const string NetworkListImplementation = "{25e509de-33d4-4447-93f8-c9e4ea381c8b}";
    private const string AssignmentType = "{9873c309-1f09-4ebf-9078-42d8057ef11b}";
    private const string BoxType = "{acfc6f68-8e3a-4af5-bf81-3dd512095a46}";
    private const string OperandInputType = "{9de7f100-1b87-424c-a62e-45b0cfc85ed2}";
    private const string OperandType = "{c9b2f165-48a2-4a45-8326-3952d8a3d708}";

    /// <summary>The output-item flag a jump carries. Read from one specimen; see the remarks.</summary>
    private const int JumpFlag = 4;

    private const string Rail = "rail";

    public static LadderDocument? TryParse(string relativePath, string content)
    {
        if (ObjectOf(relativePath, content) is not var (owner, name))
        {
            return null;
        }

        var implementation = Named(owner, "Implementation");
        if (implementation is null || Attr(implementation, "Type") != NetworkListImplementation)
        {
            return null;
        }

        LdLanguage? language = Value(implementation, "DefaultViewMode") switch
        {
            "Ld" => LdLanguage.Ladder,
            "Fbd" => LdLanguage.FunctionBlock,
            _ => null,
        };

        // Each network's file lines, for a review comment pinned to it (docs/web/PLAN.md, C3).
        var offset = LineOffset(relativePath, content);
        var networks = Items(implementation, "NetworkList")
            .Select((network, i) => Network(network, i + 1, language) with { Lines = Versio.Core.Text.SafeXml.LinesOf(network, offset) })
            .ToList();
        return new LadderDocument(new LadderBody(name, networks), Style);
    }

    /// <summary>
    /// Whether an object's implementation is the LD/FBD network list this file draws.
    /// </summary>
    internal static bool IsNetworkList(XElement owner) =>
        Named(owner, "Implementation") is { } implementation && Attr(implementation, "Type") == NetworkListImplementation;

    /// <summary>
    /// The object element and its name, from either container: a classic export's root entry,
    /// or a File-Based Storage file's content after its header. Null for anything that is not
    /// CODESYS native XML.
    /// </summary>
    /// <remarks>
    /// Shared with the member table, which reads the same object's declaration: two readers of
    /// "where is the object in this file" would be two answers waiting to disagree.
    /// </remarks>
    internal static (XElement Owner, string Name)? ObjectOf(string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        string? xml;
        string? fileName = null;
        if (relativePath.EndsWith(CodesysV3File.Extension, StringComparison.OrdinalIgnoreCase))
        {
            xml = CodesysV3File.Content(content);
            fileName = CodesysV3File.ObjectName(relativePath);
        }
        else if (relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            // git show keeps a BOM the working-tree read does not; the parser must not care
            // which side of the comparison it is reading.
            xml = content.TrimStart('\uFEFF');
        }
        else
        {
            return null;
        }

        if (xml is null)
        {
            return null;
        }

        XDocument doc;
        try
        {
            // Line info kept: the table's rows and the networks name the file lines they came from.
            doc = Versio.Core.Text.SafeXml.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }

        var (owner, name) = fileName is null ? Classic(doc) : (doc.Root, fileName);
        return owner is null ? null : (owner, name);
    }

    /// <summary>
    /// How many lines of the file come before the XML <see cref="ObjectOf"/> parsed: a File-Based
    /// Storage file's header, which the XML's own line numbers do not count. Zero for a classic
    /// export, whose file is the XML.
    /// </summary>
    /// <remarks>
    /// The XML is the file's tail (<see cref="CodesysV3File.Content"/> drops the header and the line
    /// breaks after it), so the difference in line breaks is the header's height.
    /// </remarks>
    internal static int LineOffset(string relativePath, string content)
    {
        if (!relativePath.EndsWith(CodesysV3File.Extension, StringComparison.OrdinalIgnoreCase) || CodesysV3File.Content(content) is not { } xml)
        {
            return 0;
        }

        return content.Count(c => c == '\n') - xml.Count(c => c == '\n');
    }

    /// <summary>
    /// The object element and its name in a classic export: the root entry of the export's
    /// entry list.
    /// </summary>
    /// <remarks>
    /// A recursive export lists the object's children after it. The root entry is the one
    /// marked <c>IsRoot</c>, and reading the first implementation found anywhere could draw a
    /// child method under its parent's name.
    /// </remarks>
    private static (XElement? Owner, string Name) Classic(XDocument doc)
    {
        var entries = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "List2" && Attr(e, "Name") == "EntryList")?
            .Elements()
            .ToList() ?? [];
        var root = entries.FirstOrDefault(e => Value(e, "IsRoot") == "True") ?? entries.FirstOrDefault();
        return (Named(root, "Object"), Value(Named(root, "MetaObject"), "Name"));
    }

    private static LadderNetwork Network(XElement network, int number, LdLanguage? blockLanguage)
    {
        var title = Value(network, "Title");
        var comment = Value(network, "Comment");

        // The model has no "commented out", and drawing such a network as live would show logic
        // the PLC does not run. The title says it, so it is on screen and a toggle is a change.
        if (Value(network, "OutCommented") == "True")
        {
            title = title.Length == 0 ? "(commented out)" : title + " (commented out)";
        }

        if (blockLanguage is not { } language || Value(network, "ILActive") == "True")
        {
            return new LadderNetwork { Number = number, Title = title, Comment = comment, UnsupportedLanguage = "IL" };
        }

        var builder = new NetworkBuilder(language);
        var label = Value(network, "Label");
        if (label.Length > 0)
        {
            builder.Add(new LdLabel("label", label));
        }

        foreach (var item in Items(network, "NetworkItems"))
        {
            builder.Item(item);
        }

        return new LadderNetwork
        {
            Number = number,
            Title = title,
            Comment = comment,
            Language = language,
            Nodes = builder.Nodes,
            Nets = builder.Nets,
        };
    }

    /// <summary>One network's nodes and wires, built while the expression tree is walked.</summary>
    private sealed class NetworkBuilder(LdLanguage language)
    {
        private readonly List<List<LdPinRef>> _nets = [];
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private int _synthetic;

        public List<LdNode> Nodes { get; } = [];

        public IReadOnlyList<LdNet> Nets => _nets.Where(n => n.Count >= 2).Select(n => new LdNet(n)).ToList();

        private bool Ladder => language == LdLanguage.Ladder;

        public void Add(LdNode node)
        {
            Nodes.Add(node);

            // Every id but the label's is already reserved by Id/NewId, which handed it out;
            // adding it again is harmless and is what reserves the label's.
            _ids.Add(node.Id);
        }

        public void Item(XElement item)
        {
            if (Attr(item, "Type") != AssignmentType)
            {
                // A network item this file has not seen. It stands on the rung so the network is
                // not drawn with something missing.
                var id = Id(item);
                Add(new LdUnsupported(id, "network item " + ShortType(item), [new LdPin(LdPins.In, LdPinDirection.Input)]));
                if (Ladder)
                {
                    Connect(RailOut(), new LdPinRef(id, LdPins.In));
                }

                return;
            }

            var rvalue = Named(item, "RValue");
            var outputs = OutputItems(item).Where(o => o.Name.LocalName != "Null").ToList();
            if (Ladder)
            {
                var point = rvalue is null ? RailOut() : Power(rvalue, RailOut());
                foreach (var output in outputs)
                {
                    Connect(point, new LdPinRef(Output(output), LdPins.In));
                }

                return;
            }

            var value = rvalue is null ? default : Data(rvalue);
            foreach (var output in outputs)
            {
                var input = new LdPinRef(Output(output), LdPins.In);
                if (value.Out is { } from)
                {
                    Connect(from, input);
                }
                else if (value.Operand is { } operand)
                {
                    // "x := y" in FBD: the IDE draws y as an input wired straight to the
                    // assignment. The model has no bare input element, so it is drawn as an
                    // unrecognised one carrying y. y sits on an unwired input because the layout
                    // writes an operand beside a pin only when no wire is there; on the wired
                    // output it was not drawn at all, which the first look at the drawing caught.
                    var id = NewId();
                    Add(new LdUnsupported(id, "variable",
                        [new LdPin(LdPins.In, LdPinDirection.Input, operand), new LdPin(LdPins.Out, LdPinDirection.Output)]));
                    Connect(new LdPinRef(id, LdPins.Out), input);
                }
            }
        }

        /// <summary>
        /// How deep an expression tree may nest before the rest is drawn as one
        /// unsupported element.
        /// </summary>
        /// <remarks>
        /// Far beyond anything the IDE writes — and a hard requirement all the same,
        /// because <see cref="Power"/> and <see cref="Data"/> recurse once per nesting
        /// level and a StackOverflowException is the one failure no catch can keep. The
        /// web viewer hands every repository <c>.xml</c> to this parser, and its
        /// "a throwing parser degrades to plain text" rule cannot catch an overflow: a
        /// crafted file of tens of thousands of nested <c>And</c> boxes would end the
        /// server process. Past the cap the drawing says something was cut, which is
        /// what every other shape this parser cannot draw already gets.
        /// </remarks>
        private const int MaxExpressionDepth = 128;

        private int _depth;

        /// <summary>
        /// Ladder: the elements that take power from <paramref name="from"/>, and the point
        /// power leaves them.
        /// </summary>
        private LdPinRef Power(XElement element, LdPinRef from)
        {
            if (_depth >= MaxExpressionDepth)
            {
                var deep = NewId();
                Add(new LdUnsupported(deep, "too deeply nested", TwoTerminal(null)));
                Connect(from, new LdPinRef(deep, LdPins.In));
                return new LdPinRef(deep, LdPins.Out);
            }

            _depth++;
            try
            {
                return PowerCore(element, from);
            }
            finally
            {
                _depth--;
            }
        }

        private LdPinRef PowerCore(XElement element, LdPinRef from)
        {
            switch (Attr(element, "Type"))
            {
                case OperandInputType or OperandType:
                {
                    // A contact only when CODESYS says the operand is Boolean. A TIME or an INT
                    // where power flows is a shape this file has not seen (a box fed data on its
                    // first input), and a contact on an INT would be an element nobody placed.
                    var (operand, flags, boolean) = Operand(element);
                    var id = Id(element);
                    Add((flags, boolean) switch
                    {
                        (0, true) => new LdContact(id, operand, LdContactKind.NormallyOpen),
                        (_, false) => new LdUnsupported(id, "operand (not Boolean)", TwoTerminal(operand)),
                        _ => new LdUnsupported(id, $"contact (flags {flags.ToString(CultureInfo.InvariantCulture)})", TwoTerminal(operand)),
                    });
                    Connect(from, new LdPinRef(id, LdPins.In));
                    return new LdPinRef(id, LdPins.Out);
                }

                case BoxType when Value(element, "CallType") == "And":
                    return Inputs(element).Aggregate(from, (point, input) => Power(input, point));

                case BoxType when Value(element, "CallType") == "Or":
                {
                    var ends = Inputs(element).Select(input => Power(input, from)).ToList();
                    foreach (var end in ends.Skip(1))
                    {
                        Connect(ends[0], end);
                    }

                    return ends.Count == 0 ? from : ends[0];
                }

                case BoxType:
                {
                    var box = Box(element);
                    var powered = false;
                    foreach (var (pin, input) in box.Feeds)
                    {
                        if (!powered && pin == box.PowerIn)
                        {
                            powered = true;
                            Connect(input is null ? from : Power(input, from), new LdPinRef(box.Node.Id, pin));
                            continue;
                        }

                        Feed(box, pin, input);
                    }

                    if (!powered)
                    {
                        Connect(from, new LdPinRef(box.Node.Id, box.PowerIn));
                    }

                    Add(box.Labelled());
                    return new LdPinRef(box.Node.Id, box.PowerOut);
                }

                case "" when element.Name.LocalName == "Null":
                    return from;

                default:
                {
                    var id = Id(element);
                    Add(new LdUnsupported(id, ShortType(element), TwoTerminal(null)));
                    Connect(from, new LdPinRef(id, LdPins.In));
                    return new LdPinRef(id, LdPins.Out);
                }
            }
        }

        /// <summary>
        /// A value that feeds a pin rather than carrying power: an operand to write beside the
        /// pin, or the output of a box to wire to it. All of FBD is read this way, and so are the
        /// non-power inputs of a ladder box.
        /// </summary>
        private (LdPinRef? Out, string? Operand) Data(XElement element)
        {
            // Same guard as Power, same reason; the two are the only recursion here and
            // each can reach the other through a box's feeds. Said on the pin rather
            // than silently unwired, because a drawing that quietly lost an input would
            // read as the input never having existed.
            if (_depth >= MaxExpressionDepth)
            {
                return (null, "(too deeply nested)");
            }

            _depth++;
            try
            {
                return DataCore(element);
            }
            finally
            {
                _depth--;
            }
        }

        private (LdPinRef? Out, string? Operand) DataCore(XElement element)
        {
            switch (Attr(element, "Type"))
            {
                case OperandInputType or OperandType:
                {
                    var (operand, flags, _) = Operand(element);
                    return (null, flags == 0 ? operand : $"{operand} (flags {flags.ToString(CultureInfo.InvariantCulture)})");
                }

                case BoxType:
                {
                    var box = Box(element);
                    foreach (var (pin, input) in box.Feeds)
                    {
                        Feed(box, pin, input);
                    }

                    Add(box.Labelled());
                    return (new LdPinRef(box.Node.Id, box.PowerOut), null);
                }

                case "":
                    return (null, null);

                default:
                {
                    var id = Id(element);
                    Add(new LdUnsupported(id, ShortType(element), [new LdPin(LdPins.Out, LdPinDirection.Output)]));
                    return (new LdPinRef(id, LdPins.Out), null);
                }
            }
        }

        private void Feed(PendingBox box, string pin, XElement? input)
        {
            if (input is null)
            {
                return;
            }

            var (output, operand) = Data(input);
            if (output is { } from)
            {
                Connect(from, new LdPinRef(box.Node.Id, pin));
            }
            else if (operand is not null)
            {
                box.Operands[pin] = operand;
            }
        }

        /// <summary>
        /// A box's pins from its call: the parameter names CODESYS stored, <c>EN</c>/<c>ENO</c>
        /// when the box has them, and numbered inputs for an operator, which stores none.
        /// </summary>
        private PendingBox Box(XElement element)
        {
            var id = Id(element);
            var inputs = Inputs(element).ToList();
            var inNames = Strings(Named(element, "InputParam"));
            var outNames = Strings(Named(element, "OutputParam"));
            var en = Value(element, "EN") == "True";
            var eno = Value(element, "ENO") == "True";

            if (inNames.Count == 0)
            {
                inNames = Enumerable.Range(1, Math.Max(inputs.Count, 1)).Select(i => "IN" + i.ToString(CultureInfo.InvariantCulture)).ToList();
            }

            if (outNames.Count == 0)
            {
                outNames = ["OUT"];
            }

            var feeds = new List<(string Pin, XElement? Input)>();
            var queue = new Queue<XElement>(inputs);
            if (en)
            {
                // Inferred: an EN box whose list is one longer than its parameters spends the
                // first item on EN. Otherwise EN takes power straight from the rung.
                feeds.Add(("EN", inputs.Count == inNames.Count + 1 ? queue.Dequeue() : null));
            }

            foreach (var name in inNames)
            {
                feeds.Add((name, queue.Count > 0 ? queue.Dequeue() : null));
            }

            var extra = inNames.Count;
            while (queue.Count > 0)
            {
                // Extensible inputs (ADD, AND in FBD) past the stored names.
                extra++;
                feeds.Add(("IN" + extra.ToString(CultureInfo.InvariantCulture), queue.Dequeue()));
            }

            var pins = feeds.Select(f => new LdPin(f.Pin, LdPinDirection.Input)).ToList();
            pins.AddRange(outNames.Select(n => new LdPin(n, LdPinDirection.Output)));
            if (eno)
            {
                pins.Add(new LdPin("ENO", LdPinDirection.Output));
            }

            var box = new PendingBox(
                new LdBox(id, Value(element, "BoxType"), pins, InstanceOf(element)),
                feeds,
                PowerIn: en ? "EN" : feeds[0].Pin,
                PowerOut: eno ? "ENO" : outNames[0]);

            // An output the IDE assigns to a variable (TON.ET => elapsed) is written beside its
            // pin; the list is aligned with the parameter names, and an unassigned one is null.
            var assigned = OutputItems(element).ToList();
            for (var i = 0; i < assigned.Count && i < outNames.Count; i++)
            {
                if (assigned[i].Name.LocalName != "Null")
                {
                    box.Operands[outNames[i]] = Operand(assigned[i]).Text;
                }
            }

            return box;
        }

        private string Output(XElement output)
        {
            var (operand, flags, _) = Operand(output);
            var id = Id(output);
            Add(flags switch
            {
                0 => new LdCoil(id, operand, LdCoilKind.Normal),
                JumpFlag => new LdJump(id, operand),
                _ => new LdUnsupported(id, $"output (flags {flags.ToString(CultureInfo.InvariantCulture)})",
                    [new LdPin(LdPins.In, LdPinDirection.Input), new LdPin("operand", LdPinDirection.Input, operand)]),
            });
            return id;
        }

        private LdPinRef RailOut()
        {
            if (!_ids.Contains(Rail))
            {
                Nodes.Insert(0, new LdLeftRail(Rail));
                _ids.Add(Rail);
            }

            return new LdPinRef(Rail, LdPins.Out);
        }

        private void Connect(LdPinRef a, LdPinRef b)
        {
            var first = _nets.FirstOrDefault(n => n.Contains(a));
            var second = _nets.FirstOrDefault(n => n.Contains(b));
            if (first is null && second is null)
            {
                _nets.Add([a, b]);
            }
            else if (first is null)
            {
                second!.Add(a);
            }
            else if (second is null)
            {
                first.Add(b);
            }
            else if (!ReferenceEquals(first, second))
            {
                first.AddRange(second);
                _nets.Remove(second);
            }
        }

        /// <summary>
        /// The element's own id, unique per object in CODESYS. A missing or repeated one gets a
        /// made-up id, because the model needs ids unique per network and a collision would wire
        /// two elements together.
        /// </summary>
        /// <remarks>
        /// Reserved here rather than when the node is added: a box is added after the boxes
        /// nested in its inputs, so an id not taken until then could be handed out twice.
        /// </remarks>
        private string Id(XElement element)
        {
            var id = Value(element, "Id");
            return id.Length > 0 && _ids.Add(id) ? id : NewId();
        }

        private string NewId()
        {
            string id;
            do
            {
                id = "x" + (++_synthetic).ToString(CultureInfo.InvariantCulture);
            }
            while (!_ids.Add(id));

            return id;
        }

        private static List<LdPin> TwoTerminal(string? operand)
        {
            List<LdPin> pins = [new LdPin(LdPins.In, LdPinDirection.Input), new LdPin(LdPins.Out, LdPinDirection.Output)];
            if (operand is not null)
            {
                pins.Add(new LdPin("operand", LdPinDirection.Input, operand));
            }

            return pins;
        }
    }

    /// <summary>A box whose operand labels are still being collected.</summary>
    private sealed record PendingBox(LdBox Node, List<(string Pin, XElement? Input)> Feeds, string PowerIn, string PowerOut)
    {
        public Dictionary<string, string> Operands { get; } = new(StringComparer.Ordinal);

        public LdBox Labelled() => Node with
        {
            Pins = Node.Pins.Select(p => Operands.TryGetValue(p.Name, out var operand) ? p with { Operand = operand } : p).ToList(),
        };
    }

    /// <summary>
    /// An operand's text, flags and whether CODESYS typed it Boolean, whether it is wrapped as an
    /// input (<c>{9de7f100}</c>) or bare (<c>{c9b2f165}</c>). An empty operand is shown as
    /// <c>???</c>, the way the IDE shows one; a missing Boolean reads as Boolean, which is what
    /// every operand at a power position in the fixture says.
    /// </summary>
    private static (string Text, int Flags, bool Boolean) Operand(XElement element)
    {
        var operand = Attr(element, "Type") == OperandInputType ? Named(element, "Operand") : element;
        var text = Value(operand, "Operand");
        var flags = int.TryParse(Value(Named(operand, "Flags"), "Flags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        return (text.Length == 0 ? "???" : text, flags, Value(operand, "Boolean") != "False");
    }

    private static string? InstanceOf(XElement box)
    {
        var text = Value(Named(box, "Instance"), "Operand");
        return text.Length == 0 ? null : text;
    }

    private static IEnumerable<XElement> Inputs(XElement box) =>
        Named(box, "InputItems")?.Elements() ?? [];

    private static IEnumerable<XElement> OutputItems(XElement owner) =>
        Named(Named(owner, "OutputItems"), "OutputItems")?.Elements() ?? [];

    private static IEnumerable<XElement> Items(XElement owner, string list) =>
        Named(owner, list)?.Elements() ?? [];

    private static List<string> Strings(XElement? param) =>
        Named(param, "Names")?.Elements().Select(e => e.Value).ToList() ?? [];

    /// <summary>The first block of a type GUID, enough for a reader to search the file with.</summary>
    private static string ShortType(XElement element)
    {
        var type = Attr(element, "Type").Trim('{', '}');
        var dash = type.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? type[..dash] : type.Length > 0 ? type : element.Name.LocalName;
    }

    /// <summary>The child that CODESYS's archive format names: <c>&lt;Single Name="…"&gt;</c>.</summary>
    internal static XElement? Named(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => Attr(e, "Name") == name);

    private static string Value(XElement? parent, string name) =>
        Named(parent, name) is { HasElements: false } leaf ? leaf.Value : "";

    private static string Attr(XElement? element, string name) => element?.Attribute(name)?.Value ?? "";
}
