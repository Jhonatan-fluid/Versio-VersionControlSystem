using System.Globalization;
using System.Xml.Linq;
using Versio.Core.Iec.Ladder;

namespace Versio.Plc.TiaPortal.Ladder;

/// <summary>
/// A LAD or FBD block's networks, read from this vendor's SimaticML export into the neutral
/// ladder model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both languages, one reader.</b> Portal writes LAD and FBD in the same <c>FlgNet</c>
/// schema, and one block can mix them network by network, so the language is read per compile
/// unit and set on each network. What differs is small and stays here: an <c>O</c> is a junction
/// in ladder and an OR box in FBD, and comparators are in line in ladder and boxes in FBD. The
/// drawing differs much more, and that is Core's (<c>LadderLayout.FunctionBlock.cs</c>).
/// </para>
/// <para>
/// <b>Everything TIA-shaped stops here.</b> Part names (<c>Contact</c>, <c>SCoil</c>,
/// <c>PContact</c>), the <c>FlgNet</c> network schema, the power rail as a wire end and access
/// scopes are this vendor's vocabulary. The neutral model after this file has contacts, coils,
/// boxes and nets, and nothing downstream (structure, layout, diff, drawing) learns a TIA
/// word. A second vendor's ladder is another file like this one in that vendor's pack.
/// </para>
/// <para>
/// <b>How SimaticML says a network.</b> <c>Parts</c> lists the elements (<c>Part</c>,
/// <c>Call</c>) and the operands (<c>Access</c>). <c>Wires</c> connects them. A wire with an
/// <c>IdentCon</c> carries an operand to a pin. Any other wire is one electrical node, listing
/// every pin on it, with <c>&lt;Powerrail/&gt;</c> as one of its ends when it starts at the left
/// rail: the rail is a wire end, not a part. So operands fold into the pins they label, and power
/// wires become <see cref="LdNet"/>s one to one.
/// </para>
/// <para>
/// <b>In ladder, an <c>O</c> part folds into the net.</b> Where branches rejoin, an export may write an OR
/// box whose inputs are the branch ends. In ladder that is a junction, not an instruction: its
/// pins are one electrical node, and drawing it as a box would show an element nobody placed.
/// </para>
/// <para>
/// <b>In ladder, comparators are drawn in line.</b> <c>Eq</c>, <c>Gt</c> and the rest take power on
/// <c>pre</c> and pass it on <c>out</c>, and the IDE draws them contact-sized on the rung with
/// one operand above and one below. They become <see cref="LdBox"/> with
/// <see cref="LdBox.InlineCompare"/>.
/// </para>
/// <para>
/// <b>Nothing is dropped.</b> A part this file does not know becomes <see cref="LdUnsupported"/>
/// under its own name, and a network in another language (SCL, STL) is kept as a placeholder.
/// </para>
/// <para>
/// <b>Hand-written ladder specimens, real FBD.</b> No fixture has real ladder logic yet:
/// <c>Main_1</c>'s only network is empty. The ladder specimens were written by hand against the
/// schema, which is a known gap. FBD has no such gap: the fixture's 21 FBD blocks are Portal's own
/// output, and three of them are pinned as approved drawings. See
/// <c>docs/vendors/tia/DRIVING.md</c>, "Graphical blocks".
/// </para>
/// </remarks>
public static class TiaPortalLadderParser
{
    private const string Rail = "powerrail";

    /// <summary>How TIA Portal draws ladder: networks numbered from 1, no right rail.</summary>
    public static LadderStyle Style { get; } = new() { UnitName = "Network", RightRailDrawn = false };

    private static readonly HashSet<string> CodeBlocks = new(StringComparer.Ordinal)
    {
        "SW.Blocks.OB", "SW.Blocks.FB", "SW.Blocks.FC",
    };

    private static readonly Dictionary<string, string> Comparators = new(StringComparer.Ordinal)
    {
        ["Eq"] = "==", ["Ne"] = "<>", ["Gt"] = ">", ["Ge"] = ">=", ["Lt"] = "<", ["Le"] = "<=",
    };

    /// <summary>
    /// The instructions drawn as an ordinary box. Anything else outside the contacts, coils
    /// and jumps is drawn as an unrecognised part, so a reader can see where the drawing is
    /// guessing.
    /// </summary>
    private static readonly HashSet<string> Boxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TON", "TOF", "TP", "TONR", "CTU", "CTD", "CTUD", "SR", "RS", "R_TRIG", "F_TRIG",
        "Move", "Add", "Sub", "Mul", "Div", "Mod", "Neg", "Abs", "Min", "Max", "Limit", "Inc", "Dec",
        "Convert", "Round", "Trunc", "Ceil", "Floor", "Sqr", "Sqrt", "Calc",
        "Shl", "Shr", "Rol", "Ror", "And", "Or", "Xor", "Invert", "Swap", "Fill", "MoveBlockVariant",
        "InRange", "OutRange", "Norm_X", "Scale_X",
    };

    private static readonly HashSet<string> OutputPins = new(StringComparer.OrdinalIgnoreCase)
    {
        "out", "eno", "Q", "ET", "CV", "QU", "QD", "Ret_Val", "RET_VAL",
    };

    public static LadderDocument? TryParse(string relativePath, string content)
    {
        if (string.IsNullOrWhiteSpace(content)
            || !relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        XDocument doc;
        try
        {
            // Line info kept: each network names the file lines it came from (docs/web/PLAN.md, C3).
            //
            // The byte-order mark goes first. Portal writes one, File.ReadAllText drops it, and
            // `git show` does not — so the working copy parsed and every committed copy did
            // not, the exception landed in the catch below, and the pane drew every network
            // of a committed block as added over "nothing committed to compare" (2026-10-29).
            // The member-table parser had the same trim since its first day.
            doc = Versio.Core.Text.SafeXml.Parse(content.TrimStart('\uFEFF'), LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var block = doc.Root?.Elements().FirstOrDefault(e => CodeBlocks.Contains(e.Name.LocalName));
        var attributes = block is null ? null : Child(block, "AttributeList");
        var blockLanguage = attributes is null ? "" : Text(attributes, "ProgrammingLanguage");
        if (blockLanguage is not ("LAD" or "FBD"))
        {
            return null;
        }

        var units = Children(Child(block!, "ObjectList"), "SW.Blocks.CompileUnit").ToList();
        var networks = units.Select((unit, i) => Network(unit, i + 1, blockLanguage) with { Lines = Versio.Core.Text.SafeXml.LinesOf(unit) }).ToList();

        // The framing review coverage may vouch for (docs/web/PLAN.md, C4): markup-only lines
        // outside the networks and outside the Interface, which the member table drawn over the
        // ladder vouches for itself. The block's own values (Name, Number, MemoryLayout) are text
        // between tags and stay gaps when they change.
        var drawn = networks.Select(n => n.Lines).OfType<Versio.Core.Text.LineSpan>().ToList();
        if (Child(attributes!, "Interface") is { } declaration && Versio.Core.Text.SafeXml.LinesOf(declaration) is { } d)
        {
            drawn.Add(d);
        }

        return new LadderDocument(new LadderBody(Text(attributes!, "Name"), networks), Style)
        {
            Structural = Versio.Core.Text.MarkupLines.Structural(content, drawn),
        };
    }

    /// <summary>
    /// One compile unit. Its own language decides how it is drawn: TIA Portal lets a LAD block
    /// hold FBD networks and the other way round, and a unit that names no language is the
    /// block's.
    /// </summary>
    private static LadderNetwork Network(XElement unit, int number, string blockLanguage)
    {
        var attributes = Child(unit, "AttributeList");
        var language = attributes is null ? "" : Text(attributes, "ProgrammingLanguage");
        if (language.Length == 0)
        {
            language = blockLanguage;
        }

        var title = Multilingual(unit, "Title");
        var comment = Multilingual(unit, "Comment");

        if (language is not ("LAD" or "FBD"))
        {
            return new LadderNetwork { Number = number, Title = title, Comment = comment, UnsupportedLanguage = language };
        }

        var fbd = language == "FBD";
        var drawnAs = fbd ? LdLanguage.FunctionBlock : LdLanguage.Ladder;

        var flg = attributes?.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg is null)
        {
            return new LadderNetwork { Number = number, Title = title, Comment = comment, Language = drawnAs };
        }

        var accesses = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = new List<XElement>();
        foreach (var element in Child(flg, "Parts")?.Elements() ?? [])
        {
            switch (element.Name.LocalName)
            {
                case "Access":
                    accesses[Uid(element)] = Operand(element);
                    break;
                case "Part" or "Call":
                    parts.Add(element);
                    break;
            }
        }

        // Each pin, in the order the wires first name it, with the operand wired to it.
        var pinOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var operands = new Dictionary<LdPinRef, string>();
        var nets = new List<List<LdPinRef>>();
        var railUsed = false;

        void Seen(string uid, string pin)
        {
            if (!pinOrder.TryGetValue(uid, out var list))
            {
                pinOrder[uid] = list = [];
            }

            if (!list.Contains(pin))
            {
                list.Add(pin);
            }
        }

        foreach (var wire in Children(Child(flg, "Wires"), "Wire"))
        {
            var idents = Children(wire, "IdentCon").Select(Uid).Where(accesses.ContainsKey).ToList();
            var pins = Children(wire, "NameCon").Select(n => new LdPinRef(Uid(n), Attr(n, "Name"))).ToList();
            foreach (var pin in pins)
            {
                Seen(pin.NodeId, pin.Pin);
            }

            if (idents.Count > 0)
            {
                foreach (var pin in pins)
                {
                    operands[pin] = accesses[idents[0]];
                }

                continue;
            }

            var ends = new List<LdPinRef>(pins);
            if (Children(wire, "Powerrail").Any())
            {
                ends.Add(new LdPinRef(Rail, LdPins.Out));
                railUsed = true;
            }

            if (ends.Count >= 2)
            {
                nets.Add(ends);
            }
        }

        // OR junctions: every net touching an O part becomes one net without it. Ladder only: in
        // a function block diagram O is the OR box itself, a real element with its own inputs.
        var junctions = fbd ? [] : parts.Where(p => p.Name.LocalName == "Part" && Attr(p, "Name") == "O").Select(Uid).ToList();
        foreach (var or in junctions)
        {
            var touching = nets.Where(n => n.Any(e => e.NodeId == or)).ToList();
            if (touching.Count == 0)
            {
                continue;
            }

            var merged = touching.SelectMany(n => n).Where(e => e.NodeId != or).Distinct().ToList();
            nets.RemoveAll(touching.Contains);
            nets.Add(merged);
        }

        var nodes = new List<LdNode>();
        if (railUsed)
        {
            nodes.Add(new LdLeftRail(Rail));
        }

        foreach (var part in parts)
        {
            var uid = Uid(part);
            if (junctions.Contains(uid))
            {
                continue;
            }

            var order = pinOrder.GetValueOrDefault(uid) ?? [];
            nodes.Add(part.Name.LocalName == "Call"
                ? CallBox(part, uid, order, operands)
                : fbd ? FunctionBlockNode(part, uid, order, operands) : PartNode(part, uid, order, operands));
        }

        // The two-terminal elements take the neutral pin names, so the rest of the pipeline
        // reads power flow the same way for every vendor. Their TIA pins are already "in" and
        // "out"; the operand pins never reach a net.
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        return new LadderNetwork
        {
            Number = number,
            Title = title,
            Comment = comment,
            Language = drawnAs,
            Nodes = nodes,
            Nets = nets
                .Select(n => n.Where(e => ids.Contains(e.NodeId)).ToList())
                .Where(n => n.Count >= 2)
                .Select(n => new LdNet(n))
                .ToList(),
        };
    }

    private static LdNode PartNode(XElement part, string uid, List<string> order, Dictionary<LdPinRef, string> operands)
    {
        var name = Attr(part, "Name");
        string Op(string pin) => operands.GetValueOrDefault(new LdPinRef(uid, pin)) ?? "<???>";
        string? Memory() => operands.GetValueOrDefault(new LdPinRef(uid, "bit"));
        var negated = Negated(part, "operand");

        switch (name)
        {
            case "Contact":
                return new LdContact(uid, Op("operand"), negated ? LdContactKind.NormallyClosed : LdContactKind.NormallyOpen);
            case "PContact":
                return new LdContact(uid, Op("operand"), LdContactKind.PositiveEdge, Memory());
            case "NContact":
                return new LdContact(uid, Op("operand"), LdContactKind.NegativeEdge, Memory());
            case "Coil":
                return new LdCoil(uid, Op("operand"), negated ? LdCoilKind.Negated : LdCoilKind.Normal);
            case "SCoil":
                return new LdCoil(uid, Op("operand"), LdCoilKind.Set);
            case "RCoil":
                return new LdCoil(uid, Op("operand"), LdCoilKind.Reset);
            case "PCoil":
                return new LdCoil(uid, Op("operand"), LdCoilKind.PositiveEdge, Memory());
            case "NCoil":
                return new LdCoil(uid, Op("operand"), LdCoilKind.NegativeEdge, Memory());
            case "Jump" or "JumpN":
                return new LdJump(uid, LabelName(part), IfNot: name == "JumpN");
            case "Label":
                return new LdLabel(uid, LabelName(part));
            case "Return" or "Ret":
                return new LdReturn(uid);
        }

        var pins = order
            .OrderBy(p => p.Equals("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(p => new LdPin(
                p,
                IsOutput(p) ? LdPinDirection.Output : LdPinDirection.Input,
                operands.GetValueOrDefault(new LdPinRef(uid, p)),
                Negated(part, p)))
            .ToList();

        if (Comparators.TryGetValue(name, out var symbol))
        {
            // in1 is drawn above and in2 below, whatever order the wires named them in.
            pins = pins.OrderBy(p => p.Name switch { "pre" => 0, "in1" => 1, "in2" => 2, _ => 3 }).ToList();
            return new LdBox(uid, symbol, pins, Detail: TypeTemplate(part), InlineCompare: true);
        }

        if (Boxes.Contains(name))
        {
            return new LdBox(uid, name.ToUpperInvariant(), pins, Instance(part), TypeTemplate(part));
        }

        return new LdUnsupported(uid, name, pins);
    }

    /// <summary>
    /// A part of a function block diagram: the logic gates as the IEC boxes (<c>&amp;</c>,
    /// <c>&gt;=1</c>, <c>X</c>), comparators as boxes with their pins, and everything else as
    /// ladder reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture's 127 FBD networks use A (48), Eq (47), O (27), Le, Move, Gt, Ne and Coil
    /// (66), plus 115 calls. A coil stays an <see cref="LdCoil"/>: the FBD layout draws it as the
    /// "=" assignment box, and the diff compares it with a ladder coil as the same element.
    /// </para>
    /// <para>
    /// Inputs are ordered in1, in2, in3 by number, not by which wire named them first: a gate
    /// drawn with in2 above in1 is a different drawing from the IDE's, even if the logic is
    /// the same. TIA writes no pin list for a part; the wires are all there is.
    /// </para>
    /// </remarks>
    private static LdNode FunctionBlockNode(XElement part, string uid, List<string> order, Dictionary<LdPinRef, string> operands)
    {
        var name = Attr(part, "Name");
        var gate = name switch
        {
            "A" => "&",
            "O" => ">=1",
            "X" => "X",
            _ => null,
        };

        if (gate is null && !Comparators.ContainsKey(name))
        {
            return PartNode(part, uid, order, operands);
        }

        static int Rank(string pin)
        {
            if (pin.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }

            return pin.StartsWith("in", StringComparison.OrdinalIgnoreCase)
                   && int.TryParse(pin.AsSpan(2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n
                : int.MaxValue;
        }

        var pins = order
            .OrderBy(Rank)
            .Select(p => new LdPin(
                p,
                IsOutput(p) ? LdPinDirection.Output : LdPinDirection.Input,
                operands.GetValueOrDefault(new LdPinRef(uid, p)),
                Negated(part, p)))
            .ToList();

        return gate is not null
            ? new LdBox(uid, gate, pins)
            : new LdBox(uid, "CMP " + Comparators[name], pins, Detail: TypeTemplate(part));
    }

    /// <summary>
    /// A call to a block: its parameters from the call's own interface, so a pin nothing is
    /// wired to is still drawn, the way the IDE draws an unassigned parameter.
    /// </summary>
    private static LdBox CallBox(XElement call, string uid, List<string> order, Dictionary<LdPinRef, string> operands)
    {
        var info = Child(call, "CallInfo");
        var name = info is null ? "?" : Attr(info, "Name");
        var kind = info is null ? null : Attr(info, "BlockType");
        var parameters = Children(info, "Parameter")
            .Select(p => (Name: Attr(p, "Name"), Section: Attr(p, "Section")))
            .ToList();

        var pins = new List<LdPin>();
        void Add(string pin, LdPinDirection direction)
        {
            if (pins.All(p => p.Name != pin))
            {
                pins.Add(new LdPin(pin, direction, operands.GetValueOrDefault(new LdPinRef(uid, pin)), Negated(call, pin)));
            }
        }

        if (order.Contains("en"))
        {
            Add("en", LdPinDirection.Input);
        }

        foreach (var (pin, section) in parameters)
        {
            Add(pin, section switch
            {
                "Output" or "Return" => LdPinDirection.Output,
                "InOut" => LdPinDirection.InOut,
                _ => LdPinDirection.Input,
            });
        }

        foreach (var pin in order.Where(p => p != "eno"))
        {
            Add(pin, IsOutput(pin) ? LdPinDirection.Output : LdPinDirection.Input);
        }

        if (order.Contains("eno"))
        {
            Add("eno", LdPinDirection.Output);
        }

        return new LdBox(uid, name, pins, info is null ? null : Instance(info), kind);
    }

    private static bool IsOutput(string pin) =>
        OutputPins.Contains(pin)
        || (pin.StartsWith("out", StringComparison.OrdinalIgnoreCase) && pin.Length > 3 && char.IsDigit(pin[3]));

    /// <summary>The data type an instruction is instantiated for: <c>SrcType</c>, <c>time_type</c> and the like.</summary>
    private static string? TypeTemplate(XElement part) =>
        Children(part, "TemplateValue").FirstOrDefault(t => Attr(t, "Type") == "Type")?.Value is { Length: > 0 } v ? v : null;

    private static bool Negated(XElement part, string pin) =>
        Children(part, "Negated").Any(n => Attr(n, "Name") == pin);

    private static string LabelName(XElement part) =>
        part.Descendants().FirstOrDefault(e => e.Name.LocalName == "Label")?.Attribute("Name")?.Value
        ?? part.Attribute("Label")?.Value
        ?? "?";

    private static string? Instance(XElement owner)
    {
        var instance = Child(owner, "Instance");
        if (instance is null)
        {
            return null;
        }

        var path = string.Join(".", Children(instance, "Component").Select(c => Attr(c, "Name")));
        return Attr(instance, "Scope") == "GlobalVariable" ? Quote(path) : "#" + path;
    }

    /// <summary>An operand as the IDE writes it: <c>"Tag".Member</c>, <c>#Local</c>, <c>T#5s</c>, <c>%I0.3</c>.</summary>
    private static string Operand(XElement access)
    {
        var scope = Attr(access, "Scope");
        switch (scope)
        {
            case "GlobalVariable":
                return Symbol(access, global: true);
            case "LocalVariable":
                return "#" + Symbol(access, global: false);
            case "LiteralConstant" or "TypedConstant":
                var constant = Child(access, "Constant");
                return constant is null ? scope : Text(constant, "ConstantValue");
            case "GlobalConstant":
                return Quote(Attr(Child(access, "Constant"), "Name"));
            case "LocalConstant":
                return "#" + Attr(Child(access, "Constant"), "Name");
            case "Address":
                return Address(Child(access, "Address"));
            default:
                return scope;
        }
    }

    private static string Symbol(XElement access, bool global)
    {
        var components = Children(Child(access, "Symbol"), "Component").ToList();
        return string.Join(".", components.Select((c, i) =>
        {
            var name = Attr(c, "Name");
            if (global && i == 0)
            {
                name = Quote(name);
            }

            var indices = Children(c, "Access").Select(Operand).ToList();
            return indices.Count == 0 ? name : $"{name}[{string.Join(",", indices)}]";
        }));
    }

    private static string Address(XElement? address)
    {
        if (address is null)
        {
            return "%?";
        }

        var area = Attr(address, "Area") switch
        {
            "Input" => "I",
            "Output" => "Q",
            "Memory" => "M",
            var other => other,
        };
        var offset = int.TryParse(Attr(address, "BitOffset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit) ? bit : 0;
        var byteOffset = (offset / 8).ToString(CultureInfo.InvariantCulture);
        return Attr(address, "Type") switch
        {
            "Bool" => $"%{area}{byteOffset}.{(offset % 8).ToString(CultureInfo.InvariantCulture)}",
            "Byte" => $"%{area}B{byteOffset}",
            "Word" or "Int" or "UInt" => $"%{area}W{byteOffset}",
            _ => $"%{area}D{byteOffset}",
        };
    }

    private static string Quote(string name) => "\"" + name + "\"";

    /// <summary>
    /// A compile unit's title or comment, in English when there is English, else the first
    /// language that has text.
    /// </summary>
    /// <remarks>
    /// The export does not say which language the project edits in, and the fixture carries
    /// de-DE first with the same text in every language. English first is the order that reads
    /// for the people reviewing in this app; a project with no English still shows its text.
    /// </remarks>
    private static string Multilingual(XElement unit, string composition)
    {
        var text = Children(Child(unit, "ObjectList"), "MultilingualText")
            .FirstOrDefault(m => Attr(m, "CompositionName") == composition);
        var items = text?.Descendants().Where(e => e.Name.LocalName == "MultilingualTextItem")
            .Select(i => Child(i, "AttributeList"))
            .Where(a => a is not null)
            .Select(a => (Culture: Text(a!, "Culture"), Text: Text(a!, "Text")))
            .Where(i => i.Text.Length > 0)
            .ToList() ?? [];
        return items.FirstOrDefault(i => i.Culture == "en-US").Text ?? items.FirstOrDefault().Text ?? "";
    }

    private static string Uid(XElement element) => Attr(element, "UId");

    private static string Attr(XElement? element, string name) => element?.Attribute(name)?.Value ?? "";

    private static string Text(XElement parent, string local) => Child(parent, local)?.Value.Trim() ?? "";

    private static XElement? Child(XElement? parent, string local) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == local);

    private static IEnumerable<XElement> Children(XElement? parent, string local) =>
        parent?.Elements().Where(e => e.Name.LocalName == local) ?? [];
}
