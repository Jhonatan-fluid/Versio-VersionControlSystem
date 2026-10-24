using System.Globalization;
using System.Text;
using Versio.Core.Diff;
using Versio.Core.Plc;

namespace Versio.Core.Projects;

/// <summary>One label/value pair of a card.</summary>
public sealed record CardFact(string Label, string Value);

/// <summary>
/// The facts a card answers, built once for both hosts.
/// </summary>
/// <remarks>
/// The desktop's <c>TaskDetailRender</c>, <c>DeviceDetailRender</c> and <c>ProjectSummaryRender</c>
/// each listed their rows in the view model, and the web had no cards at all, so a task opened on
/// the web was its raw file. The rows are decided here now; each host draws them (XAML there, HTML
/// through <see cref="CardHtml"/> here).
/// </remarks>
public static class Cards
{
    private const string Dash = "—";

    /// <summary>One task, opened from its row: everything its catalog records about it.</summary>
    public static IReadOnlyList<CardFact> Task(TaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return
        [
            new("Priority", Blank(task.Priority)),
            new("Interval", Blank(task.DisplaySchedule)),
            new("Core binding", task.CoreBinding switch
            {
                "" => Dash,
                "-1" => "-1 (not bound to a core)",
                var c => c,
            }),
            new("Watchdog", task.WatchdogEnabled
                ? $"enabled · time {Blank(task.WatchdogTime)} · sensitivity {Blank(task.WatchdogSensitivity)}"
                : "disabled"),
            new("Calls", task.Pous.Count == 0 ? Dash : string.Join(", ", task.Pous)),
        ];
    }

    /// <summary>
    /// One connector, parameter or empty slot, opened from its row. The catalog is the file; this
    /// is the highlight. Selecting the device object itself renders the whole catalog table, the
    /// same split task rows make between a task card and the task catalog.
    /// </summary>
    public static IReadOnlyList<CardFact> Device(DeviceCatalog catalog, ProjectTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(node);

        var facts = new List<CardFact> { new("Device", catalog.Name) };
        var path = node.Path;
        var slotAt = path.LastIndexOf(DeviceCatalog.SlotRowMarker, StringComparison.Ordinal);
        var paramAt = path.LastIndexOf(DeviceCatalog.ParamRowMarker, StringComparison.Ordinal);
        var connAt = path.LastIndexOf(DeviceCatalog.ConnectorRowMarker, StringComparison.Ordinal);

        if (slotAt >= 0)
        {
            facts.Insert(0, new("Slot", path[(slotAt + DeviceCatalog.SlotRowMarker.Length)..]));
        }
        else if (paramAt >= 0)
        {
            facts.Insert(0, new("Value", node.Detail.Length > 0 ? node.Detail : Dash));
        }
        else if (connAt >= 0)
        {
            var key = path[(connAt + DeviceCatalog.ConnectorRowMarker.Length)..];

            // KeyedConnectors, matching the tree's row paths: colliding keys carry #2/#3 there, so
            // a raw-Key lookup would open the first duplicate's facts for both.
            var connector = catalog.KeyedConnectors()
                .FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.Ordinal))
                .Value;
            if (connector is not null)
            {
                if (connector.ConnectorId.Length > 0)
                {
                    facts.Add(new("Connector id", connector.ConnectorId));
                }

                if (connector.ModuleType.Length > 0)
                {
                    facts.Add(new("Module type", connector.ModuleType));
                }

                facts.Add(new("Parameters", connector.Parameters.Count.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return facts;
    }

    /// <summary>
    /// The project card: what a glance at the row should answer, from what a commit holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rows both hosts can know: the manifest, the catalogs, the coverage. The desktop adds
    /// the rows only a disk and a machine can answer (the project file's size and save time, the
    /// last export, whether the toolchain is installed) and keeps its own order; the web draws
    /// these. The one fact people will look for and not find is which IDE version last saved the
    /// project: the manifest does not record it, and saying nothing beats guessing.
    /// </para>
    /// <para>
    /// <b>Which toolchain wrote this mirror</b> is a different question from whether one is
    /// installed: a colleague's export, a CI machine's, or an older IDE all read the same on the
    /// vendor row and differently here. Verbatim, because the manifest recorded it verbatim and
    /// shortening it would be inventing a parse of somebody else's version string. Absent on a
    /// mirror exported before the manifest carried it, and the row is simply not drawn — "not
    /// recorded" is what an empty field means, and "unknown" would claim more than that.
    /// </para>
    /// <para>
    /// A source project (a folder of readable files saved by the vendor's IDE) gets its own rows
    /// rather than the tracked ones with gaps: nothing is ever exported from it, no toolchain is
    /// involved, and the vendor is whoever claimed the folder.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CardFact> Project(WorkspaceProject project, int fileCount)
    {
        ArgumentNullException.ThrowIfNull(project);
        var manifest = project.Manifest;

        if (PlcFormats.ForSourceProject(project.ProjectPath) is { } source)
        {
            var sourceFacts = new List<CardFact>
            {
                new("Vendor", source.DisplayName),
                new("Stored as", "its own readable files, saved by the vendor's IDE"),
                new("Sync", "none. Versio shows and draws this project; it never exports, imports or locks it"),
                new("Project folder", $"{fileCount} file(s)"),
            };
            if (manifest is not null)
            {
                sourceFacts.Add(new("Objects", Objects(manifest)));
            }

            return sourceFacts;
        }

        var facts = new List<CardFact> { new("Vendor", (manifest?.Vendor ?? PlcVendor.Codesys).ToString()) };
        if (manifest?.ToolchainVersion is { Length: > 0 } producedBy)
        {
            facts.Add(new("Exported by", producedBy));
        }

        facts.Add(new("Sync state", project.ExportState.Length > 0 ? project.ExportState : "tracked"));
        if (manifest is null)
        {
            facts.Add(new("Mirror", "never exported"));
            return facts;
        }

        facts.Add(new("Mirror", $"{manifest.Objects.Count} exported file(s) · manifest schema {manifest.Schema}"));
        facts.Add(new("Objects", Objects(manifest)));

        var references = project.Libraries.Select(l => l.Current).Where(c => c.IsValid).ToList();
        if (references.Count > 0)
        {
            facts.Add(new("Libraries", $"{references.Sum(c => c.References.Count)} reference(s) in {references.Count} manager(s)"));
        }

        if (project.Tasks.Select(t => t.Current).FirstOrDefault(t => t.IsValid) is { } tasks)
        {
            facts.Add(new("Tasks", string.Join(", ", tasks.Tasks.Select(t => $"{t.Name} ({t.DisplaySchedule})"))));
        }

        facts.Add(new("Coverage", ExportCoverage.Of(manifest) is { HasGaps: true } report
            ? report.Describe()
            : "complete — every object the exporter understands is in the mirror"));

        if (manifest.Warnings.Count > 0)
        {
            facts.Add(new("Warnings", $"{manifest.Warnings.Count} — see the log"));
        }

        return facts;
    }

    /// <summary>
    /// One entry per OBJECT, not per file — a GVL is two files and one object, and a summary that
    /// counted it twice would disagree with the tree beside it.
    /// </summary>
    private static string Objects(ProjectManifest manifest) =>
        string.Join(", ", manifest.Objects
            .Where(o => !string.IsNullOrEmpty(o.Path))
            .GroupBy(o => string.IsNullOrEmpty(o.Guid) ? o.Path : o.Guid, StringComparer.Ordinal)
            .GroupBy(g => g.First().Type, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => ObjectTypeNames.Counted(manifest.Vendor, g.Key, g.Count())));

    private static string Blank(string value) => value.Length == 0 ? Dash : value;
}

/// <summary>
/// A card of facts as an HTML fragment: the desktop's label/value list, for a browser.
/// </summary>
/// <remarks>
/// In the <see cref="MemberTableHtml"/> pattern: every string goes through <see cref="CodeHtml"/>,
/// so a hidden character in a task name or a library title is marked rather than obeyed, and the
/// page carries the file the card was read from in <c>data-src-path</c>. Rows carry
/// <c>data-fact</c> with the label, the contract a link attaches to.
/// </remarks>
public static class CardHtml
{
    /// <summary>The fragment for a card.</summary>
    /// <param name="kind">project, task or device: the page's <c>data-kind</c>.</param>
    /// <param name="sourcePath">The file the facts were read from; empty for a project card.</param>
    public static string Fragment(string kind, string title, IReadOnlyList<CardFact> facts, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(facts);

        var hidden = 0;
        var body = new StringBuilder(facts.Count * 96 + 256);
        body.Append("<div class=\"pc-title\">");
        hidden += CodeHtml.AppendLine(body, title, SyntaxLanguage.PlainText);
        body.Append("</div>\n<table class=\"pc-facts\">\n<tbody>\n");
        foreach (var fact in facts)
        {
            body.Append("<tr class=\"pc-fact\" data-fact=\"").Append(CodeHtml.A(fact.Label)).Append("\"><th class=\"pc-label\">");
            hidden += CodeHtml.AppendLine(body, fact.Label, SyntaxLanguage.PlainText);
            body.Append("</th><td class=\"pc-value\">");
            hidden += CodeHtml.AppendLine(body, fact.Value, SyntaxLanguage.PlainText);
            body.Append("</td></tr>\n");
        }

        body.Append("</tbody>\n</table>\n");

        var html = new StringBuilder(body.Length + 128);
        html.Append("<div class=\"pc-page\" data-kind=\"").Append(CodeHtml.A(kind))
            .Append("\" data-src-path=\"").Append(CodeHtml.A(sourcePath))
            .Append("\" data-hidden-chars=\"").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
        CodeHtml.AppendNotice(html, hidden);
        html.Append(body).Append("</div>\n");
        return html.ToString();
    }
}
