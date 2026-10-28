using Versio.Core.Text;

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

    /// <summary>
    /// The file lines this member is declared on in the version the row describes, when the
    /// format knows them; for a compared row, the newer version's. Null for a removed row.
    /// </summary>
    /// <remarks>
    /// Where a review comment on the row is pinned (docs/web/PLAN.md, *Pull request review*, C3),
    /// in <see cref="MemberTable.SourcePath"/>. A placement, never evidence of what the row shows.
    /// </remarks>
    public LineSpan? Lines { get; init; }

    /// <summary>
    /// The lines the member was declared on in the OLDER version of a comparison: set for
    /// unchanged, modified and removed rows, so a comment can sit on either side.
    /// </summary>
    public LineSpan? WasLines { get; init; }

    /// <summary>How this row differs from the last commit; <see cref="MemberChange.None"/> when unknown.</summary>
    public MemberChange Change { get; init; }

    /// <summary>
    /// What the row said at the last commit, for a modified or removed row; empty otherwise.
    /// Shown on hover, so the table answers "changed from what" without a trip to Raw.
    /// </summary>
    public string Was { get; init; } = string.Empty;

    /// <summary>
    /// A modified row's committed value for each field that changed; null where the field did
    /// not. The pane draws old → new in exactly the cells that moved, so a reader sees
    /// <i>what</i> changed, not only that the row did (owner, 2026-10-05).
    /// </summary>
    public string? DatatypeBefore { get; init; }

    /// <inheritdoc cref="DatatypeBefore"/>
    public string? StartValueBefore { get; init; }

    /// <inheritdoc cref="DatatypeBefore"/>
    public string? ExternalAccessBefore { get; init; }

    /// <inheritdoc cref="DatatypeBefore"/>
    public string? CommentBefore { get; init; }

    public bool DatatypeChanged => DatatypeBefore is not null;

    public bool StartValueChanged => StartValueBefore is not null;

    public bool ExternalAccessChanged => ExternalAccessBefore is not null;

    public bool CommentChanged => CommentBefore is not null;

    /// <summary>The marker column's letter: the tree's own A / M / D, blank when unchanged.</summary>
    public string ChangeGlyph => Change switch
    {
        MemberChange.Added => "A",
        MemberChange.Modified => "M",
        MemberChange.Removed => "D",
        _ => string.Empty,
    };

    /// <summary>The marker's tooltip, in words, so the letter is never the only explanation.</summary>
    public string ChangeLabel => Change switch
    {
        MemberChange.Added => "Added since the last commit",
        MemberChange.Modified => "Modified since the last commit: changed cells show old → new",
        MemberChange.Removed => "Deleted since the last commit",
        _ => string.Empty,
    };

    public bool IsAdded => Change == MemberChange.Added;

    public bool IsModified => Change == MemberChange.Modified;

    public bool IsRemoved => Change == MemberChange.Removed;

    /// <summary>What the NAME column draws: the leaf, padded by depth.</summary>
    public string DisplayName => new string('\u00A0', Math.Max(0, Depth) * 2) + Name;

    /// <summary>
    /// The attributes written on this member alone (<c>{attribute 'symbol' := 'readwrite'}</c>
    /// above <c>Scada</c>), in the order the file has them; after a comparison, removed ones too.
    /// </summary>
    /// <remarks>
    /// Drawn as chips under the name rather than as a column: most members have none, and a
    /// column empty on nearly every row takes its width from the comment (owner, 2026-10-23).
    /// Shown as written. Which attribute wins over the block's own is the vendor's rule, and
    /// Core does not apply it.
    /// </remarks>
    public IReadOnlyList<MemberAttribute> Attributes { get; init; } = [];

    public bool HasAttributes => Attributes.Count > 0;
}

/// <summary>
/// One attribute: a name and, when it has one, a value, as the file spelled them. Applies to
/// a whole object (<see cref="MemberTable.Attributes"/>) or to one member
/// (<see cref="MemberRow.Attributes"/>).
/// </summary>
/// <remarks>
/// Opaque on purpose, like <see cref="MemberRow.Section"/>: the format fills it from whatever
/// its language calls an attribute, and Core never interprets a name.
/// </remarks>
public sealed record MemberAttribute
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The value, unquoted; empty for an attribute that is only a name (<c>'hide'</c>).</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>The file lines it is written on, when the format knows them; null for a removed one.</summary>
    public LineSpan? Lines { get; init; }

    /// <summary>The lines it was written on in the OLDER version of a comparison, as <see cref="MemberRow.WasLines"/>.</summary>
    public LineSpan? WasLines { get; init; }

    /// <summary>The value it had on the older side of a comparison: <see cref="Value"/> unless it was changed.</summary>
    public string OlderValue => ValueBefore is null ? Value : ValueBefore == "(empty)" ? "" : ValueBefore;

    /// <summary>How it differs from the last commit; <see cref="MemberChange.None"/> when unknown.</summary>
    public MemberChange Change { get; init; }

    /// <summary>The committed value of a modified attribute ("(empty)" for none); null otherwise.</summary>
    public string? ValueBefore { get; init; }

    /// <summary>
    /// A keyword on the object's header (<c>PUBLIC</c>, <c>ABSTRACT</c>) rather than a pragma
    /// written above it. The same chip, drawn beside the title instead of in the attribute strip;
    /// see <see cref="MemberTable.Modifiers"/>.
    /// </summary>
    public bool IsModifier { get; init; }

    /// <summary>What the chip says: <c>symbol = readwrite</c>, or the bare name.</summary>
    public string Text => Value.Length == 0 ? Name : $"{Name} = {Value}";

    /// <summary>The chip's tooltip, so the change never rests on colour alone.</summary>
    public string ChangeLabel => Change switch
    {
        MemberChange.Added => "Added since the last commit",
        MemberChange.Modified => $"Changed since the last commit: was {ValueBefore}",
        MemberChange.Removed => "Deleted since the last commit",
        _ => IsModifier ? "Modifier on the header" : "Attribute",
    };

    public bool IsAdded => Change == MemberChange.Added;

    public bool IsModified => Change == MemberChange.Modified;

    public bool IsRemoved => Change == MemberChange.Removed;
}

/// <summary>
/// A line of a declaration the table does not draw: a doc comment above the header, an
/// <c>EXTENDS</c>, a comment between two sections, a pragma that is not an attribute.
/// </summary>
/// <param name="Line">The file line, from 1.</param>
/// <param name="Text">Its code and comment, with spacing collapsed: re-indenting is not a change worth a warning.</param>
public sealed record UnshownLine(int Line, string Text);

/// <summary>How a member row differs from the same object at the last commit.</summary>
public enum MemberChange
{
    None,
    Added,
    Modified,
    Removed,
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

    /// <summary>
    /// The file the rows' <see cref="MemberRow.Lines"/> are lines of: the layer the table was
    /// parsed from, which need not be the file on screen (a block's table comes from the
    /// <c>.xml</c> beside its <c>.st</c>). Set by the host, which knows which layer it parsed.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

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
    /// One sentence on what changed since the last commit, or empty when nothing did or
    /// nobody compared. Set by <see cref="MemberTableDiff.Compare"/>.
    /// </summary>
    public string ChangeSummary { get; init; } = string.Empty;

    public bool HasChangeSummary => ChangeSummary.Length > 0;

    /// <summary>
    /// The attributes written on the object itself (<c>{attribute 'linkalways'}</c> above the
    /// header); after a comparison, removed ones too.
    /// </summary>
    /// <remarks>
    /// The owner, 2026-10-23, on F_UpdateMeta: the tree said M, Raw showed one new
    /// <c>{attribute 'linkalways'}</c>, and the Rendered tab showed nothing at all, because the
    /// declaration reader dropped every pragma. A strip of chips under the title now.
    /// </remarks>
    public IReadOnlyList<MemberAttribute> Attributes { get; init; } = [];

    public bool HasAttributes => Attributes.Count > 0;

    /// <summary>
    /// The keywords on the object's header between its kind and its name (<c>METHOD PUBLIC
    /// Reset</c>: <c>PUBLIC</c>; <c>FUNCTION_BLOCK ABSTRACT FB</c>: <c>ABSTRACT</c>), in the order
    /// written; after a comparison, removed ones too. Empty for a format whose headers have none.
    /// </summary>
    /// <remarks>
    /// The second time the Rendered tab hid a header change. On 2026-10-23 a method header that
    /// gained <c>PUBLIC</c> made its empty table warn "1 declaration change is not shown" beside a
    /// code block that showed every change, and the warning went: the modifier was ruled framing,
    /// and the cost — a modifier change neither drawn nor warned about — was written on the parser.
    /// On 2026-10-28 the same change on <c>FB_Template.Reset</c> was found that way: the tree said M,
    /// Raw showed <c>METHOD Reset</c> become <c>METHOD PUBLIC Reset</c>, and Rendered drew an empty
    /// interface and the unchanged code. Drawn now, as chips beside the title, marked as attribute
    /// chips are: a keyword gained is green, one taken away is struck. Nothing warns, because
    /// nothing is hidden.
    /// </remarks>
    public IReadOnlyList<MemberAttribute> Modifiers { get; init; } = [];

    public bool HasModifiers => Modifiers.Count > 0;

    /// <summary>
    /// The file line the title and <see cref="Modifiers"/> are read from, when the format knows
    /// it; null otherwise. For review coverage: a modifier chip drawn as changed vouches for the
    /// header on both sides, and the chip itself is written on one side only.
    /// </summary>
    public LineSpan? Header { get; init; }

    /// <summary><see cref="Header"/> in the OLDER version of a comparison. Set by <see cref="MemberTableDiff.Compare"/>.</summary>
    public LineSpan? HeaderBefore { get; init; }

    /// <summary>
    /// Every declaration line the table does not draw, so a comparison can tell when one of
    /// them changed. Empty for a format that does not report them.
    /// </summary>
    public IReadOnlyList<UnshownLine> Unshown { get; init; } = [];

    /// <summary>
    /// One sentence when the declaration changed in a line this table does not draw, or empty.
    /// Set by <see cref="MemberTableDiff.Compare"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrower than the note the owner had removed on 2026-10-11 ("changed, but not in its
    /// members"): that one fired for any change outside the rows, including the code under the
    /// table, which marks itself. This fires only for the declaration's own lines, which
    /// nothing else on the pane draws, so it is said only when the pane would otherwise hide a
    /// change.
    /// </para>
    /// <para>
    /// The bug it is the net for: F_UpdateMeta's attribute was missing from the Rendered tab
    /// and nothing said so. Teaching the table attributes fixed that case; this catches the
    /// next thing the table does not draw, whatever it is.
    /// </para>
    /// </remarks>
    public string UnshownChange { get; init; } = string.Empty;

    public bool HasUnshownChange => UnshownChange.Length > 0;

    /// <summary>
    /// The file lines that are the declaration's own framing in the version this table was read
    /// from: the header, the section keywords (<c>VAR_INPUT</c>, <c>END_VAR</c>, <c>END_TYPE</c>),
    /// blank lines, and for an XML file the markup around and between the rows. Empty for a
    /// format that does not report them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For review coverage (docs/web/PLAN.md, *Pull request review*, C4): a change to one of these
    /// lines is vouched for by the table, because there is nothing a cell could show differently
    /// and nothing a reviewer sent to Raw would learn — a <c>VAR_IN_OUT</c> block opened, a method
    /// header that gained <c>PUBLIC</c>, a blank line removed between two declarations. The owner's
    /// real pull request (2026-10-23) listed every one of those as "changed, but not shown".
    /// </para>
    /// <para>
    /// Declared by the parser, which is the only party that knows what its syntax's framing is;
    /// a line it did not declare stays a gap. Not <see cref="Unshown"/>: those are lines that say
    /// something the table does not draw (a doc comment, an <c>EXTENDS</c>), and a change to them
    /// is one the reviewer must go to Raw for.
    /// </para>
    /// </remarks>
    public IReadOnlyList<LineSpan> Structural { get; init; } = [];

    /// <summary>
    /// <see cref="Structural"/> for the OLDER version of a comparison, so a framing line taken away
    /// is vouched for on the side it was on. Set by <see cref="MemberTableDiff.Compare"/>.
    /// </summary>
    public IReadOnlyList<LineSpan> StructuralBefore { get; init; } = [];

    /// <summary>
    /// <see cref="Unshown"/> for the OLDER version of a comparison: what the table said it does not
    /// draw there. Review coverage refuses to vouch for any line on either list, whatever row's span
    /// it falls in — a pragma between a row's comment and its declaration sits inside the row's
    /// span and is drawn nowhere (review, 2026-10-24).
    /// </summary>
    public IReadOnlyList<UnshownLine> UnshownBefore { get; init; } = [];

    /// <summary>The layout with its name: a bare "Optimized" under a title read as a status.</summary>
    public string MemoryLayoutCaption =>
        MemoryLayout.Length == 0 ? string.Empty : "Memory layout: " + MemoryLayout;

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

    /// <summary>
    /// A list of global variables (a GVL, a tag table) rather than a data block or a type.
    /// </summary>
    /// <remarks>
    /// The IEC notion again, not a vendor's: storage declared outside any block. It changes the
    /// note under the title only. Calling a global variable list "the data block interface"
    /// would name an object that vendor does not have.
    /// </remarks>
    public bool IsVariableList { get; init; }

    /// <summary>
    /// Whether any row says who may reach it from outside (an HMI, OPC). Only some vendors have
    /// the notion, and a column that is empty on every row of every table of a vendor is noise:
    /// the pane leaves it out rather than draw it blank.
    /// </summary>
    public bool HasExternalAccess => Members.Any(m => m.ExternalAccess.Length > 0);

    /// <summary>
    /// Whether any row has a type. A list that is not declarations (a text list, a settings
    /// list) has none, and the pane leaves the column out rather than draw it empty.
    /// </summary>
    public bool HasDatatype => Members.Any(m => !m.IsSectionHeading && m.Datatype.Length > 0);

    /// <summary>
    /// The name column's header, when the rows are not variables: "TEXT ID" for a text list.
    /// Null for the default.
    /// </summary>
    public string? NameLabel { get; init; }

    /// <summary>The value column's header when the IDE calls it something of its own. Null for the default.</summary>
    public string? ValueLabel { get; init; }

    /// <summary>The note under the title when none of the kinds above says it right. Null for the default.</summary>
    public string? Note { get; init; }

    /// <summary>The name column's header.</summary>
    public string NameHeader => NameLabel ?? "NAME";

    /// <summary>The value column's header: what the IDE calls it for this kind of object.</summary>
    public string ValueHeader => ValueLabel ?? (IsDataType || IsInterface ? "DEFAULT VALUE" : "START VALUE");

    /// <summary>The one-line note under the title.</summary>
    public string ReadOnlyNote => Note ?? (IsDataType, IsInterface, IsVariableList) switch
    {
        (true, _, _) => "Read-only view of the data type's members. This table does not write back.",
        (_, true, _) => "Read-only view of the block interface. A linked type opens that object; a highlighted row's type has uncommitted changes.",
        (_, _, true) => "Read-only view of the global variables. This table does not write back.",
        _ => "Read-only view of the data block interface. This table does not write back.",
    };
}
