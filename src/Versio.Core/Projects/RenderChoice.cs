using Versio.Core.Iec.Ladder;
using Versio.Core.Plc;

namespace Versio.Core.Projects;

/// <summary>What a host draws for a tree row.</summary>
public enum RenderKind
{
    /// <summary>Nothing can be drawn: no file, an unreadable one, or binary.</summary>
    Nothing,

    /// <summary>The project row's card of facts.</summary>
    ProjectCard,

    /// <summary>A library manager's whole table.</summary>
    LibraryCatalog,

    /// <summary>A task configuration's whole table.</summary>
    TaskCatalog,

    /// <summary>One task, opened from its row.</summary>
    TaskCard,

    /// <summary>A device's whole table of connectors.</summary>
    DeviceCatalog,

    /// <summary>One connector, parameter or empty slot, opened from its row.</summary>
    DeviceCard,

    /// <summary>A member table; a block interface carries its code underneath (<see cref="RenderChoice.Code"/>).</summary>
    MemberTable,

    /// <summary>A ladder block with no interface table above it.</summary>
    Ladder,

    /// <summary>An object whose own file holds nothing to read: the note in <see cref="EmptyObjectNote"/>.</summary>
    EmptyObject,

    /// <summary>A Markdown document as a page.</summary>
    Markdown,

    /// <summary>An HTML file: its markup as source, and a page the host may frame.</summary>
    WebPage,

    /// <summary>The file laid out as the IDE numbers it.</summary>
    Source,
}

/// <summary>What sits under a block interface's table.</summary>
public enum CodeUnder
{
    /// <summary>Nothing: a data type, a variable list, or code that could not be read.</summary>
    None,

    /// <summary>The networks, drawn.</summary>
    Ladder,

    /// <summary>The code body only (<see cref="RenderChoice.Body"/>), stripped of its declaration.</summary>
    Body,

    /// <summary>The whole file: the format knows no body in it (a vendor's native XML, a graphical block's text).</summary>
    WholeFile,
}

/// <summary>
/// The one decision of what a tree row's Rendered view draws, made for both hosts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is Core's.</b> The desktop pane and the web viewer each had a copy of this
/// decision, and the copies drifted: the web had no project card, no catalogs, no husk rule and no
/// web-page tab, and in a pull request it drew a directory-form project's container objects, which
/// hold nothing (an application, a communication manager, a server), and its library list as
/// raw metadata and XML, where the app drew a note and a table (FINDINGS 2026-10-23, the owner's
/// library pull request). When two hosts draw one thing, the decision of what is drawn belongs
/// here and the host only draws. <c>RenderChoiceParityTests</c> runs both hosts over every object
/// of every fixture project and asserts the same choice.
/// </para>
/// <para>
/// <b>Pure.</b> The host reads files; this is handed a reader and the registered formats and
/// touches nothing else, so it is testable without a disk, git or GitHub, and
/// <c>VendorSeamTests</c> keeps it free of a vendor's name.
/// </para>
/// <para>
/// <b>The order is the desktop's</b>, which the owner shaped row by row: the project card; a
/// reference, task or device row opens its catalog or its own card; Versio's YAML catalogs; the
/// vendor's own catalog files; a member table from any format over any layer, with the block's
/// ladder, code body or whole file underneath when it is an interface; a ladder alone; the
/// empty-object note; Markdown; a web page; the source. Each arm's reasoning is beside it.
/// </para>
/// </remarks>
public sealed record RenderChoice
{
    public static readonly RenderChoice Nothing = new() { Kind = RenderKind.Nothing };

    public required RenderKind Kind { get; init; }

    /// <summary>The file the drawing comes from: the layer a table or ladder was parsed from, the primary file for text.</summary>
    public string? File { get; init; }

    /// <summary>
    /// Every file the drawing was read from, for re-reading at another version: one, or one per
    /// task for a configuration saved as a file per task. Empty for a card.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The format that read <see cref="Model"/>; null for Versio's own YAML, Markdown and plain source.</summary>
    public IPlcProjectFormat? Format { get; init; }

    /// <summary>
    /// What was parsed: a <see cref="LibraryCatalog"/>, <see cref="TaskCatalog"/>, <see cref="TaskRecord"/>,
    /// <see cref="DeviceCatalog"/>, <see cref="MemberTable"/> or <see cref="LadderDocument"/>. Null for the rest.
    /// </summary>
    public object? Model { get; init; }

    /// <summary>
    /// Reads the same model from other copies of <see cref="Files"/>, in that order: the committed
    /// copy on the desktop, the older commit on the web. The same reader as <see cref="Model"/>'s,
    /// so marks compare like with like; a source project's table read by its vendor's format must
    /// not be compared with a YAML reader's answer (the desktop, 2026-10-10).
    /// </summary>
    public Func<IReadOnlyList<string?>, object?>? Reparse { get; init; }

    /// <summary>What sits under an interface table.</summary>
    public CodeUnder Code { get; init; }

    /// <summary>The file the code under the table comes from: the object's primary file.</summary>
    public string? CodeFile { get; init; }

    /// <summary>The ladder under the table, when <see cref="Code"/> is <see cref="CodeUnder.Ladder"/>.</summary>
    public LadderDocument? CodeLadder { get; init; }

    /// <summary>The body's lines, when <see cref="Code"/> is <see cref="CodeUnder.Body"/>.</summary>
    public (int Start, int End)? Body { get; init; }

    /// <summary>
    /// Said over a source that stands in for a drawing Versio does not have: a vendor's native file
    /// for an object no model draws (CFC, SFC, a visualisation, a tag table). Null when the source is
    /// the object's own text, which needs no apology.
    /// </summary>
    public string? Notice { get; init; }

    /// <summary>The default-valued record a parameterless construction gives.</summary>
    private RenderChoice()
    {
    }

    /// <summary>The choice for a row.</summary>
    /// <param name="node">The row's node: its kind, files, name, children and type.</param>
    /// <param name="read">A layer's text, or null when it cannot be read; a NUL in the text means binary.</param>
    /// <param name="primary">
    /// The file the text arms draw (the empty-object note, Markdown, the page, the source) and the
    /// code under a table comes from. The node's primary file unless the host shows another layer.
    /// </param>
    /// <param name="formats">The registered formats; <see cref="PlcFormats.All"/> unless a test says otherwise.</param>
    public static RenderChoice Decide(
        ProjectTreeNode node, Func<string, string?> read, string? primary = null, IReadOnlyList<IPlcProjectFormat>? formats = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(read);
        formats ??= PlcFormats.All;
        primary ??= node.PrimaryFile;

        string? CatalogFile(string suffix) => node.Files.FirstOrDefault(
            f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        switch (node.Kind)
        {
            case ProjectNodeKind.Project:
                return new RenderChoice { Kind = RenderKind.ProjectCard };

            // A reference or task row renders its whole catalog: the row's own facts are in the
            // table, highlighted by being the thing the user just clicked at.
            case ProjectNodeKind.Library
                when CatalogFile(LibraryCatalog.FileSuffix) is { } yaml
                     && read(yaml) is { } text
                     && LibraryCatalog.Parse(text) is { IsValid: true } catalog:
                return Yaml(RenderKind.LibraryCatalog, yaml, catalog);

            case ProjectNodeKind.Task
                when CatalogFile(TaskCatalog.FileSuffix) is { } yaml
                     && read(yaml) is { } text
                     && TaskCatalog.Parse(text).Tasks.FirstOrDefault(t => t.Name == node.Name) is { } task:
                return new RenderChoice { Kind = RenderKind.TaskCard, File = yaml, Files = [yaml], Model = task };

            case ProjectNodeKind.Device
                when CatalogFile(DeviceCatalog.FileSuffix) is { } yaml
                     && read(yaml) is { } text
                     && DeviceCatalog.Parse(text) is { IsValid: true } catalog:
                return new RenderChoice { Kind = RenderKind.DeviceCard, File = yaml, Files = [yaml], Model = catalog };

            case ProjectNodeKind.Object or ProjectNodeKind.File:
                return ObjectOrFile(node, read, primary, formats);

            // A folder has nothing of its own to draw; a reference, task or device row whose
            // catalog cannot be read has nothing either, and inventing a view would claim one.
            default:
                return Nothing;
        }
    }

    private static RenderChoice Yaml(RenderKind kind, string file, object model) =>
        new()
        {
            Kind = kind,
            File = file,
            Files = [file],
            Model = model,
            // Read back by the reader its suffix names, as the desktop's YamlSource did.
            Reparse = heads =>
                file.EndsWith(LibraryCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase) ? LibraryCatalog.Parse(heads[0])
                : file.EndsWith(TaskCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase) ? TaskCatalog.Parse(heads[0])
                : DeviceCatalog.Parse(heads[0]),
        };

    private static RenderChoice ObjectOrFile(ProjectTreeNode node, Func<string, string?> read, string? primary, IReadOnlyList<IPlcProjectFormat> formats)
    {
        string? CatalogFile(string suffix) => node.Files.FirstOrDefault(
            f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (CatalogFile(LibraryCatalog.FileSuffix) is { } libraries
            && read(libraries) is { } libText
            && LibraryCatalog.Parse(libText) is { IsValid: true } libCatalog)
        {
            return Yaml(RenderKind.LibraryCatalog, libraries, libCatalog);
        }

        if (CatalogFile(TaskCatalog.FileSuffix) is { } tasks
            && read(tasks) is { } taskText
            && TaskCatalog.Parse(taskText) is { IsValid: true } taskCatalog)
        {
            return Yaml(RenderKind.TaskCatalog, tasks, taskCatalog);
        }

        if (CatalogFile(DeviceCatalog.FileSuffix) is { } devices
            && read(devices) is { } devicesText
            && DeviceCatalog.Parse(devicesText) is { IsValid: true } devicesCatalog)
        {
            return Yaml(RenderKind.DeviceCatalog, devices, devicesCatalog);
        }

        // A vendor's own files for the same three things: a source project has no Versio YAML,
        // and before this its library manager, devices and tasks drew as raw source (the owner,
        // 2026-10-10). The committed copy is read by the same format, so the marks compare like
        // with like.
        if (VendorCatalog(node, read, formats) is { } vendorCatalog)
        {
            return vendorCatalog;
        }

        // Every registered format, every layer of this object. The readable sibling is often the
        // primary file; the table lives in a native one beside it. Asking the primary file alone
        // would highlight that text and never offer the table. The format that does not
        // understand the bytes returns null and costs a suffix check.
        //
        // Deleting this loop chooses Source for a data block and MemberTablePaneFlowTests goes
        // red — the same class of silent-green as a dropped subscription.
        foreach (var format in formats)
        {
            foreach (var file in node.Files)
            {
                if (read(file) is { } text
                    && format.TryParseMemberTable(file, text) is { IsValid: true } parsed
                    // A table with nothing in it is a husk, not an interface: a parameterless
                    // method tabled column headers over a blank (seen on a real library,
                    // 2026-10-14) where its code should be. Falling through draws the source
                    // with its sections, which is what a type with no members has always got.
                    // The cost is the rare block whose members were ALL deleted: its struck rows
                    // live in Raw rather than a rendered table. Section headings do not count —
                    // Members carries them as rows, and a heading over nothing is the same husk.
                    //
                    // EXCEPT a code block's interface, since 2026-10-23: the owner wants a POU
                    // with no variables (a library's Main, VAR END_VAR) drawn as every other POU
                    // is, an empty table over its code. The husk's harm was the table standing
                    // where the code should be, and an interface now always carries its code
                    // underneath, so that harm cannot recur. Types and variable lists keep the
                    // rule.
                    && (parsed.IsInterface || parsed.Members.Any(m => !m.IsSectionHeading)))
                {
                    var choice = new RenderChoice
                    {
                        Kind = RenderKind.MemberTable,
                        File = file,
                        Files = [file],
                        Format = format,
                        Model = parsed,
                        Reparse = heads => heads[0] is { } head ? format.TryParseMemberTable(file, head) : null,
                    };

                    return parsed.IsInterface ? CodeUnderTable(choice, format, read, primary) : choice;
                }
            }
        }

        // A ladder block with no table above it (an organisation block has no interface the
        // table draws): the networks alone. Every format, every layer, as for the table, and
        // before the source layout, which would otherwise highlight the vendor's XML. Deleting
        // this loop turns LadderPaneFlowTests red.
        foreach (var format in formats)
        {
            foreach (var file in node.Files)
            {
                if (read(file) is { } text && format.TryParseLadder(file, text) is { } ladder)
                {
                    return new RenderChoice
                    {
                        Kind = RenderKind.Ladder,
                        File = file,
                        Files = [file],
                        Format = format,
                        Model = ladder,
                        Reparse = heads => heads[0] is { } head ? format.TryParseLadder(file, head) : null,
                    };
                }
            }
        }

        if (primary is null || read(primary) is not { } primaryText)
        {
            return Nothing;
        }

        // An object with nothing in its own file (a communication manager, the logic root of a
        // device): the note says so, where one line of archive XML read as a view that had failed
        // (owner, 2026-10-23). Raw keeps the file. Deleting this arm turns
        // EmptyObjectPaneFlowTests red.
        if (formats.Any(f => f.HoldsNoSettings(primary, primaryText)))
        {
            return new RenderChoice { Kind = RenderKind.EmptyObject, File = primary, Files = [primary] };
        }

        // A NUL says binary — an image or a compiled blob dropped into the repository — and a
        // code renderer fed one draws a wall of boxes. The note above is the one text arm that
        // reads a file a format vouched for, so it comes first.
        if (primaryText.Contains('\0'))
        {
            return Nothing;
        }

        // Markdown before the source layout, which would otherwise take it: an ST layout of a
        // document is the raw text with line numbers, which is what Raw already shows. Deleting
        // this arm turns MarkdownPaneFlowTests red.
        if (IsMarkdown(primary))
        {
            return new RenderChoice { Kind = RenderKind.Markdown, File = primary, Files = [primary] };
        }

        // An HTML file is its markup, laid out as source, AND a page the host may frame — the
        // desktop's third chip (owner, 2026-10-04). Only .html and .htm: an .svg or .xml would
        // also load in a browser, but nobody asked to view an XML file as a web page, and every
        // extension added here is another kind of repository content handed to a browser engine.
        if (IsWebPage(primary))
        {
            return new RenderChoice { Kind = RenderKind.WebPage, File = primary, Files = [primary] };
        }

        return new RenderChoice { Kind = RenderKind.Source, File = primary, Files = [primary], Notice = NativeNotice(node, primary) };
    }

    /// <summary>
    /// What goes under an interface table: the block's ladder when its primary file is one, else
    /// its code body, else the whole file. A code block is its interface AND its code: the table
    /// alone would take the code out of the Rendered tab it was always in, so the source rides
    /// underneath it. A ladder block's code is a diagram, not text, drawn under its table the way
    /// the IDE shows the interface above the networks.
    /// </summary>
    private static RenderChoice CodeUnderTable(RenderChoice choice, IPlcProjectFormat format, Func<string, string?> read, string? primary)
    {
        if (primary is null || read(primary) is not { } text || text.Contains('\0'))
        {
            return choice;
        }

        if (format.TryParseLadder(primary, text) is { } ladder)
        {
            return choice with { Code = CodeUnder.Ladder, CodeFile = primary, CodeLadder = ladder };
        }

        // Only the body: the declaration above it is the table, and the BEGIN / END_ marks around
        // it say nothing a reader needs. The format says where the body is (owner, 2026-10-05).
        // A format that knows no body in this file — a block saved as the vendor's XML, a
        // graphical block's text header — gives the whole file, as the desktop always has; the
        // web drew the table with nothing under it until 2026-10-23.
        return format.CodeBody(primary, text) is { } body
            ? choice with { Code = CodeUnder.Body, CodeFile = primary, Body = body }
            : choice with { Code = CodeUnder.WholeFile, CodeFile = primary };
    }

    /// <summary>
    /// A library manager, device or task read by a vendor's format from its own file, or a task
    /// configuration whose child objects are tasks; null when no format reads any of them.
    /// </summary>
    private static RenderChoice? VendorCatalog(ProjectTreeNode node, Func<string, string?> read, IReadOnlyList<IPlcProjectFormat> formats)
    {
        foreach (var format in formats)
        {
            foreach (var file in node.Files)
            {
                if (read(file) is not { } text)
                {
                    continue;
                }

                if (format.TryParseLibraryCatalog(file, text) is { IsValid: true } libraries)
                {
                    return new RenderChoice
                    {
                        Kind = RenderKind.LibraryCatalog,
                        File = file,
                        Files = [file],
                        Format = format,
                        Model = libraries,
                        Reparse = heads => heads[0] is { } head ? format.TryParseLibraryCatalog(file, head) : null,
                    };
                }

                if (format.TryParseDeviceCatalog(file, text) is { IsValid: true } device)
                {
                    return new RenderChoice
                    {
                        Kind = RenderKind.DeviceCatalog,
                        File = file,
                        Files = [file],
                        Format = format,
                        Model = device,
                        Reparse = heads => heads[0] is { } head ? format.TryParseDeviceCatalog(file, head) : null,
                    };
                }

                if (format.TryParseTask(file, text) is { } task)
                {
                    return new RenderChoice
                    {
                        Kind = RenderKind.TaskCard,
                        File = file,
                        Files = [file],
                        Format = format,
                        Model = task,
                        Reparse = heads => heads[0] is { } head ? format.TryParseTask(file, head) : null,
                    };
                }
            }

            // A configuration saved as one file per task: its table is its children's tasks, in
            // the order the tree has them.
            var children = node.Children
                .Where(c => c.Files.Count > 0)
                .Select(c => (File: c.Files[0], Text: read(c.Files[0])))
                .Where(c => c.Text is not null)
                .Select(c => (c.File, Task: format.TryParseTask(c.File, c.Text!)))
                .Where(c => c.Task is not null)
                .ToList();
            if (children.Count > 0)
            {
                var files = children.Select(c => c.File).ToList();
                return new RenderChoice
                {
                    Kind = RenderKind.TaskCatalog,
                    File = node.PrimaryFile ?? files[0],
                    Files = files,
                    Format = format,
                    Model = new TaskCatalog
                    {
                        Schema = 1,
                        Configuration = node.Name,
                        Path = node.Path,
                        Tasks = children.Select(c => c.Task!).ToList(),
                    },
                    Reparse = heads => new TaskCatalog
                    {
                        Schema = 1,
                        Configuration = node.Name,
                        Tasks = heads.Select((head, i) => string.IsNullOrEmpty(head) ? null : format.TryParseTask(files[i], head)).OfType<TaskRecord>().ToList(),
                    },
                };
            }
        }

        return null;
    }

    /// <summary>The extensions read as Markdown. Anything else keeps its own renderer.</summary>
    public static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>The extensions a host may frame as a page.</summary>
    public static bool IsWebPage(string path) =>
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The notice over a vendor's native file drawn as source because no model draws its object.
    /// </summary>
    /// <remarks>
    /// Only for an object whose primary file is not its own text: an ST program's source is its
    /// drawing, and a plain file of the repository (a README's neighbour, a tool's config) has no
    /// object to apologise for. The object's type is named as the tree names it, so a reader of a
    /// CFC program's XML is told it is a program Versio has no drawing for, not shown XML with
    /// nothing said — silent XML is what the owner saw on the real pull request (FINDINGS
    /// 2026-10-23).
    /// </remarks>
    public static string? NativeNotice(ProjectTreeNode node, string file)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(file);
        if (node.Kind != ProjectNodeKind.Object || PlcFormats.LanguageFor(file) == Diff.LanguageCatalog.StructuredText)
        {
            return null;
        }

        var what = string.IsNullOrEmpty(node.TypeName) ? "object" : node.TypeName;
        return $"Versio has no drawing for this {what} yet; this is its file as the vendor saved it.";
    }
}
