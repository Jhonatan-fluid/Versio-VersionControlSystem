using System.IO;
using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Iec.Ladder;
using Versio.Core.Projects;
using Versio.Core.Text;

namespace Versio.App.ViewModels;

/// <summary>One label/value pair of a rendered summary.</summary>
public sealed record FactRow(string Label, string Value);

/// <summary>
/// A file's own content, shaped for the diff control to draw.
/// </summary>
/// <remarks>
/// <para>
/// The pane's renderer is <c>DiffView</c> — virtualized, gutter-numbered, syntax-coloured —
/// so rendering content reuses the one code-drawing control instead of growing a second.
/// </para>
/// <para>
/// <b>Two shapes of the same file, one per tab.</b> <see cref="PseudoDiff"/> is the file as a
/// diff of all-context lines: the Raw tab shows a clean file through it, <c>@@</c> header
/// and file line numbers included, because Raw is where the file is shown as the file.
/// <see cref="Rows"/> is what the Rendered tab draws, and it used to be the pseudo-diff too —
/// which is how the Rendered view came to carry a blue <c>@@ -1,47 +1,47 @@</c> line, two
/// gutters that always agreed, and an empty marker column, none of which say anything about
/// a file that is not being compared with anything. The rows are laid out the way the IDE
/// shows the object instead; see <see cref="StSourceLayout"/>.
/// </para>
/// </remarks>
public sealed record SourceRender
{
    public required string Path { get; init; }
    public required string PseudoDiff { get; init; }

    /// <summary>The Rendered tab's rows: sectioned and numbered as the IDE numbers them.</summary>
    public required IReadOnlyList<SourceRow> Rows { get; init; }

    /// <summary>Said out loud on the rendered view: the file is clean right now.</summary>
    public bool NoChanges { get; init; }

    /// <summary>Read one file as a rendered source view. Null when it cannot be read.</summary>
    public static SourceRender? Load(string absolutePath, string repoRelativePath, bool noChanges)
    {
        string text;
        try
        {
            text = File.ReadAllText(absolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or FileNotFoundException or DirectoryNotFoundException)
        {
            // Mid-swap, deleted, or unreadable — the raw tab's diff still says what git
            // knows, and a render that threw over a torn file would take the pane down on
            // every export.
            return null;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');

        var builder = new System.Text.StringBuilder(text.Length + lines.Length + 32);
        builder.Append("@@ -1,").Append(lines.Length)
               .Append(" +1,").Append(lines.Length).Append(" @@\n");
        foreach (var line in lines)
        {
            builder.Append(' ').Append(line).Append('\n');
        }

        return new SourceRender
        {
            Path = repoRelativePath,
            PseudoDiff = builder.ToString(),
            Rows = StSourceLayout.Lay(text),
            NoChanges = noChanges,
        };
    }
}

/// <summary>
/// A Markdown file as a page: headings, lists, tables and links, not the text that spells them.
/// </summary>
/// <remarks>
/// Before this a <c>.md</c> fell through to <see cref="SourceRender"/>, so its Rendered tab drew
/// the same pipes and asterisks as Raw (seen 2026-10-04 on <c>docs/outpost/PLAN.md</c>). The
/// parse is <see cref="Markdown"/> in Core; drawing it is <c>MarkdownView</c>.
/// </remarks>
public sealed record MarkdownRender
{
    public required string Path { get; init; }
    public required IReadOnlyList<MdBlock> Blocks { get; init; }

    /// <summary>The extensions read as Markdown. Anything else keeps its own renderer.</summary>
    public static bool Handles(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    public static MarkdownRender From(string repoRelativePath, string text) =>
        new() { Path = repoRelativePath, Blocks = Markdown.Parse(text) };
}

/// <summary>
/// A code block's Rendered view: its interface as a table, its code underneath.
/// </summary>
/// <remarks>
/// The table is what changed on 2026-10-04 (FB members as rows, user types linked); the code
/// is what the tab always showed, kept so tabling the interface did not take it away.
/// </remarks>
public sealed record InterfaceRender
{
    public required MemberTable Table { get; init; }

    /// <summary>The code as text. Null when the code is drawn as <see cref="Ladder"/> instead.</summary>
    public SourceRender? Source { get; init; }

    /// <summary>The code as a ladder diagram, for a block written in ladder.</summary>
    public LadderRender? Ladder { get; init; }

    /// <summary>What sits under the table: the diagram when there is one, else the text.</summary>
    public object? Code => (object?)Ladder ?? Source;
}

/// <summary>
/// A ladder block's Rendered view: its networks drawn, the committed version beside the working
/// one.
/// </summary>
/// <remarks>
/// <para>
/// Everything vendor-shaped happened before this record: a format's
/// <c>TryParseLadder</c> read the file, and <c>Versio.Core.Iec.Ladder</c> compared it. This holds
/// only the result and the vendor's drawing conventions, and <c>LadderPageView</c> draws it.
/// </para>
/// <para>
/// <b><see cref="Pending"/>.</b> A changed block's committed copy is a git read, done after the
/// pane is shown, as the member table's marks are. Until it lands the page draws nothing. Drawing
/// the block as "no changes" for a moment would be a claim that is false.
/// </para>
/// </remarks>
public sealed record LadderRender
{
    public required string Path { get; init; }

    public required LadderComparison Comparison { get; init; }

    public required LadderStyle Style { get; init; }

    public bool Pending { get; init; }
}

/// <summary>The project row's rendered view: the facts a glance should answer.</summary>
public sealed record ProjectSummaryRender
{
    public required string Name { get; init; }
    public required IReadOnlyList<FactRow> Facts { get; init; }
}

/// <summary>One task, opened from its row: everything its catalog records about it.</summary>
public sealed record TaskDetailRender
{
    public required TaskRecord Task { get; init; }

    public IReadOnlyList<FactRow> Facts =>
        new List<FactRow>
        {
            new("Priority", Blank(Task.Priority)),
            new("Interval", Blank(Task.DisplaySchedule)),
            new("Core binding", Task.CoreBinding switch
            {
                "" => "—",
                "-1" => "-1 (not bound to a core)",
                var c => c,
            }),
            new("Watchdog", Task.WatchdogEnabled
                ? $"enabled · time {Blank(Task.WatchdogTime)} · sensitivity {Blank(Task.WatchdogSensitivity)}"
                : "disabled"),
            new("Calls", Task.Pous.Count == 0 ? "—" : string.Join(", ", Task.Pous)),
        };

    private static string Blank(string value) => value.Length == 0 ? "—" : value;
}

/// <summary>
/// One connector, parameter or empty slot, opened from its row.
/// </summary>
/// <remarks>
/// The catalog is the file; this is the highlight. Selecting the device object itself
/// renders the whole <see cref="DeviceCatalog"/> table, the same split task rows already
/// make between <see cref="TaskDetailRender"/> and <see cref="TaskCatalog"/>.
/// </remarks>
public sealed record DeviceDetailRender
{
    public required string Title { get; init; }
    public required IReadOnlyList<FactRow> Facts { get; init; }
}
