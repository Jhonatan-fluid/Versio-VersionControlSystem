namespace Versio.Core.Projects;

/// <summary>
/// One row of a structured member pane: a name, a type, a start value, external
/// access, a comment.
/// </summary>
/// <remarks>
/// Flattened from whatever nested shape the format parsed. The pane indents by
/// <see cref="Depth"/> and the dotted <see cref="Path"/> is what a reviewer greps.
/// </remarks>
public sealed record MemberRow
{
    /// <summary>Dotted path from the block root, including array indexes.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The member's own name, without its parents.</summary>
    public string Name { get; init; } = string.Empty;

    public string Datatype { get; init; } = string.Empty;

    /// <summary>
    /// The section this member was declared in, as the file spelled it.
    /// </summary>
    /// <remarks>
    /// Opaque on purpose. Formats fill this from their own section vocabulary; Core
    /// does not interpret it.
    /// </remarks>
    public string Section { get; init; } = string.Empty;

    public string StartValue { get; init; } = string.Empty;

    /// <summary>
    /// How this member is reached from outside the program, as the format spelled it.
    /// </summary>
    /// <remarks>
    /// Opaque on purpose. Formats fill this from their own access vocabulary; Core
    /// does not interpret it. Empty when the format has nothing to say.
    /// </remarks>
    public string ExternalAccess { get; init; } = string.Empty;

    public string Comment { get; init; } = string.Empty;

    /// <summary>Nesting depth, zero at the block root. Drives indent in the pane.</summary>
    public int Depth { get; init; }

    /// <summary>
    /// The name of a user-defined type or block this member's type refers to, as the format
    /// recognised it in <see cref="Datatype"/>; empty for elementary types.
    /// </summary>
    /// <remarks>
    /// Filled by the format, because only the format knows how its language spells a
    /// reference to a user type. Core never parses <see cref="Datatype"/>.
    /// </remarks>
    public string TypeReference { get; init; } = string.Empty;

    /// <summary>
    /// A heading row naming a section of a block interface (inputs, outputs, ...), with no
    /// member of its own. <see cref="Name"/> carries the section's name.
    /// </summary>
    public bool IsSectionHeading { get; init; }

    /// <summary>
    /// The object <see cref="TypeReference"/> resolved to in this workspace, or null when it
    /// names nothing the export wrote (a library or system type). Filled by the host, which
    /// is the only party holding the object index.
    /// </summary>
    public string? LinkGuid { get; init; }

    /// <summary>
    /// The referenced object has uncommitted changes. Filled by the host with
    /// <see cref="LinkGuid"/>: a member whose type changed underneath it is a change to this
    /// block's meaning that its own diff does not show.
    /// </summary>
    public bool ReferenceChanged { get; init; }

    public bool HasLink => LinkGuid is not null;

    /// <summary>What the NAME column draws: the leaf, padded by depth.</summary>
    public string DisplayName => new string('\u00A0', Math.Max(0, Depth) * 2) + Name;
}

/// <summary>
/// A structured pane for one object's members, produced by a project format on open.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vendor-neutral on purpose, and not a file on disk.</b> Catalogs are YAML the export
/// wrote; this is a view a format builds from a file it already understands. The pane
/// dispatches by type the same way it does for <see cref="LibraryCatalog"/>, and Core
/// does not know which format produced the rows.
/// </para>
/// <para>
/// <see cref="InstanceOf"/> is empty when the block is not an instance of something.
/// <see cref="MemoryLayout"/> is whatever string the format filled in, unread here.
/// </para>
/// </remarks>
public sealed record MemberTable
{
    public string Name { get; init; } = string.Empty;

    public string Number { get; init; } = string.Empty;

    public string InstanceOf { get; init; } = string.Empty;

    public string MemoryLayout { get; init; } = string.Empty;

    public IReadOnlyList<MemberRow> Members { get; init; } = [];

    /// <summary>A named block, even one with no members yet.</summary>
    public bool IsValid => Name.Length > 0;

    public bool HasInstanceOf => InstanceOf.Length > 0;

    public string InstanceCaption =>
        InstanceOf.Length == 0 ? string.Empty : "Instance of " + InstanceOf;

    public bool HasNumber => Number.Length > 0;

    public string NumberCaption =>
        Number.Length == 0 ? string.Empty : "Number " + Number;

    public bool HasMemoryLayout => MemoryLayout.Length > 0;

    /// <summary>
    /// A user-defined data type rather than a data block: a declaration with no storage of
    /// its own.
    /// </summary>
    /// <remarks>
    /// The IEC 61131-3 notion, not a vendor's: a structure type declared once and used by the
    /// blocks that hold values. It changes two words on the pane and nothing else - a type's
    /// initial values are defaults every instance starts from, not the start values of one
    /// block, and calling the table "the data block interface" would name the wrong object.
    /// Added 2026-10-04 when data types first got a table (they showed as plain text before).
    /// </remarks>
    public bool IsDataType { get; init; }

    /// <summary>
    /// A code block's interface - its parameters and locals - rather than storage.
    /// </summary>
    /// <remarks>
    /// Rows come in sections (inputs, outputs, in-outs, statics, temporaries, constants), each
    /// opened by an <see cref="MemberRow.IsSectionHeading"/> row. The block's code is not here;
    /// the host shows it beside the table.
    /// </remarks>
    public bool IsInterface { get; init; }

    /// <summary>The value column's header: what the IDE calls it for this kind of object.</summary>
    public string ValueHeader => IsDataType || IsInterface ? "DEFAULT VALUE" : "START VALUE";

    /// <summary>The one-line note under the title.</summary>
    public string ReadOnlyNote => (IsDataType, IsInterface) switch
    {
        (true, _) => "Read-only view of the data type's members. This table does not write back.",
        (_, true) => "Read-only view of the block interface. A linked type opens that object; a highlighted row's type has uncommitted changes.",
        _ => "Read-only view of the data block interface. This table does not write back.",
    };
}
