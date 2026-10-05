namespace Versio.Core.Iec.Ladder;

/// <summary>
/// One block's ladder, as a vendor's parser read it, and how that vendor's IDE draws it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a neutral model at all.</b> Ladder is an IEC 61131-3 language: every vendor draws a
/// rail, contacts, coils, boxes and branches. What differs is the file each vendor writes and a
/// handful of drawing conventions. The file is code and lives in the vendor's pack, where one
/// parser maps it into this model. The conventions are data (<see cref="LadderStyle"/>), so the
/// layout, the diff and the drawing never branch on a vendor and never learn its name.
/// </para>
/// <para>
/// <b>A graph, not a tree.</b> TIA Portal writes parts and wires, and so does PLCopen XML (which
/// TwinCAT uses and CODESYS can export). A graph is what they have in common, and a vendor
/// whose format is already a tree (CODESYS's native network list, Rockwell's rung text) turns
/// into one trivially. <see cref="LadderStructure"/> recovers the series/parallel shape the IDE
/// draws, and falls back to a plainer placement when a network has no such shape.
/// </para>
/// </remarks>
public sealed record LadderDocument(LadderBody Body, LadderStyle Style);

/// <summary>A block's networks, in the order the IDE shows them.</summary>
public sealed record LadderBody(string BlockName, IReadOnlyList<LadderNetwork> Networks);

/// <summary>
/// The drawing conventions a vendor's IDE uses, which a reviewer expects to see again here.
/// </summary>
/// <remarks>
/// Values only. A convention that needed code would be a vendor leaking past the seam; a
/// new value here is the seam growing.
/// </remarks>
public sealed record LadderStyle
{
    /// <summary>What the IDE calls one network: "Network" in TIA Portal, "Rung" elsewhere.</summary>
    public string UnitName { get; init; } = "Network";

    /// <summary>
    /// Whether the IDE draws a right power rail. TIA Portal does not; most others do.
    /// </summary>
    public bool RightRailDrawn { get; init; }

    public static LadderStyle Default { get; } = new();
}

/// <summary>One network (rung), numbered as the IDE numbers it.</summary>
public sealed record LadderNetwork
{
    /// <summary>The IDE's number. Positional in every vendor seen so far, so it shifts on a delete.</summary>
    public required int Number { get; init; }

    public string Title { get; init; } = "";

    public string Comment { get; init; } = "";

    public IReadOnlyList<LdNode> Nodes { get; init; } = [];

    public IReadOnlyList<LdNet> Nets { get; init; } = [];

    /// <summary>
    /// Set when this network is written in another language inside a ladder block (TIA
    /// Portal allows SCL or STL networks in a LAD block). It is drawn as a labelled
    /// placeholder: dropping it would show a block with a network missing.
    /// </summary>
    public string? UnsupportedLanguage { get; init; }
}

/// <summary>An element of a network. <see cref="Id"/> is unique within its network only.</summary>
public abstract record LdNode(string Id);

/// <summary>The left power rail, where power flow starts.</summary>
public sealed record LdLeftRail(string Id) : LdNode(Id);

public enum LdContactKind { NormallyOpen, NormallyClosed, PositiveEdge, NegativeEdge }

/// <summary>A contact: passes power when its operand is true (or false, for normally closed).</summary>
/// <param name="EdgeMemory">The bit an edge contact remembers its last state in, where the vendor names one.</param>
public sealed record LdContact(string Id, string Operand, LdContactKind Kind, string? EdgeMemory = null)
    : LdNode(Id);

public enum LdCoilKind { Normal, Negated, Set, Reset, PositiveEdge, NegativeEdge }

/// <summary>A coil: writes power flow to its operand.</summary>
public sealed record LdCoil(string Id, string Operand, LdCoilKind Kind, string? EdgeMemory = null)
    : LdNode(Id);

public enum LdPinDirection { Input, Output, InOut }

/// <summary>
/// One pin of a box. A pin that appears in a <see cref="LdNet"/> carries power flow; one with
/// an <see cref="Operand"/> is drawn as a label beside the box, the way every IDE draws it.
/// </summary>
public sealed record LdPin(string Name, LdPinDirection Direction, string? Operand = null, bool Negated = false);

/// <summary>
/// A box: a timer, counter, move, maths, a call to a function or function block, or a
/// comparator.
/// </summary>
/// <param name="Detail">
/// A second line under the type name, such as the data type an instruction is instantiated
/// for ("Int", "Time").
/// </param>
/// <param name="InlineCompare">
/// A comparator the IDE draws in line on the rung, contact-sized, with its two operands above
/// and below rather than as a box with pins.
/// </param>
public sealed record LdBox(
    string Id,
    string TypeName,
    IReadOnlyList<LdPin> Pins,
    string? Instance = null,
    string? Detail = null,
    bool InlineCompare = false) : LdNode(Id);

/// <summary>A jump to a label, ending its rung like a coil.</summary>
public sealed record LdJump(string Id, string Target, bool IfNot = false) : LdNode(Id);

/// <summary>A jump target. Stands at the head of its network, wired to nothing.</summary>
public sealed record LdLabel(string Id, string Name) : LdNode(Id);

/// <summary>A return from the block, ending its rung like a coil.</summary>
public sealed record LdReturn(string Id) : LdNode(Id);

/// <summary>
/// An element the vendor's parser does not know. Drawn as a grey box with the vendor's own
/// name for it, never left out: a diagram missing an element is a diagram that lies.
/// </summary>
public sealed record LdUnsupported(string Id, string VendorName, IReadOnlyList<LdPin> Pins) : LdNode(Id);

/// <summary>One end of a wire: a node's pin.</summary>
public readonly record struct LdPinRef(string NodeId, string Pin);

/// <summary>One electrical node: every pin listed here is connected to every other.</summary>
public sealed record LdNet(IReadOnlyList<LdPinRef> Ends);

/// <summary>The pin names of the two-terminal elements and the rail.</summary>
/// <remarks>
/// Neutral names a parser maps its own onto, so the structure code reads power flow the same
/// way for every vendor.
/// </remarks>
public static class LdPins
{
    public const string In = "in";
    public const string Out = "out";
}
