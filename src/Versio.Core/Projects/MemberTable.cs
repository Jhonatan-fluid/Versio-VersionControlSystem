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

    /// <summary>The value column's header: what the IDE calls it for this kind of object.</summary>
    public string ValueHeader => IsDataType ? "DEFAULT VALUE" : "START VALUE";

    /// <summary>The one-line note under the title.</summary>
    public string ReadOnlyNote => IsDataType
        ? "Read-only view of the data type's members. This table does not write back."
        : "Read-only view of the data block interface. This table does not write back.";
}
