using System.Globalization;
using System.Text;
using Versio.Core.Diff;
using Versio.Core.Review;

namespace Versio.Core.Projects;

/// <summary>
/// A library manager, task configuration or device as an HTML fragment: the desktop's three
/// catalog tables, for a browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same columns as the desktop's templates, in the same order</b>: NAME, RESOLUTION,
/// NAMESPACE, KIND for references; TASK, INTERVAL, PRIO, WATCHDOG, CALLS for tasks; CONNECTOR, ID,
/// PARAMETERS for a device's connectors, with its empty slots listed under them. The caption under
/// the title is the desktop's too: a reference and a device table are export-only; a task table is
/// the importable half of the mirror.
/// </para>
/// <para>
/// <b>A modified row shows old then new in exactly the cells that moved</b>, from the
/// <see cref="RowMark"/> that <see cref="CatalogDiff"/> put on it, as <see cref="MemberTableHtml"/>
/// does for members: the A / M / D marker, a struck removed row, the old value struck before the
/// new. The change summary, when the comparison wrote one, stands above the table.
/// </para>
/// <para>
/// <b>Every cell goes through <see cref="CodeHtml"/></b>: escaped, and a hidden character in a
/// library title or a task name shown as a marker. Each row carries <c>data-key</c> (the reference's
/// name, the task's name, the connector's key) and <c>data-anchors</c>, the review anchors it stands
/// for, one per side it exists on; the page carries the file in <c>data-src-path</c>. One rule
/// decides both (<see cref="Doms"/>), as for members, so the anchor list and the markup cannot
/// disagree about which row is which. The rows carry no line span: the catalog readers do not
/// record where in the YAML or the vendor's file a row came from, so a comment on a row is placed by
/// its key and fingerprint, never by a line.
/// </para>
/// </remarks>
public static class CatalogHtml
{
    /// <summary>The caption under a library manager's title, the desktop's words.</summary>
    public const string LibraryNote = "Export-only: edit libraries in the IDE. This file records what the project asks for, never how it resolved here.";

    /// <summary>The caption under a task configuration's title, the desktop's words.</summary>
    public const string TaskNote = "The importable half: edit the .tasks.yaml and the next import applies it. Tasks are never created or deleted by an import.";

    /// <summary>The caption under a device's title, the desktop's words.</summary>
    public const string DeviceNote = "Export-only: edit devices in the IDE. This file records connectors, parameters and empty slots for review.";

    /// <summary>The fragment for a library manager, compared or not.</summary>
    /// <param name="compared">A comparison: rows exist on the older side too, and say so in their anchors.</param>
    /// <param name="sourcePath">The file the rows were read from, for <c>data-src-path</c>; the catalog's own path when empty.</param>
    public static string Fragment(LibraryCatalog catalog, bool compared = false, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Table("libraries", catalog.Manager, LibraryNote, catalog.ChangeSummary, Source(sourcePath, catalog.Path),
            ["NAME", "RESOLUTION", "NAMESPACE", "KIND"],
            catalog.References.Select((r, i) => new RowCells(r.Name, r.Mark, Doms(r.Mark, i, compared),
                [(r.DisplayVersion, r.ResolutionBefore), (r.Namespace, r.NamespaceBefore), (r.Kind, r.KindBefore)])).ToList(),
            []);
    }

    /// <summary>The fragment for a task configuration, compared or not.</summary>
    public static string Fragment(TaskCatalog catalog, bool compared = false, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Table("tasks", catalog.Configuration, TaskNote, catalog.ChangeSummary, Source(sourcePath, catalog.Path),
            ["TASK", "INTERVAL", "PRIO", "WATCHDOG", "CALLS"],
            catalog.Tasks.Select((t, i) => new RowCells(t.Name, t.Mark, Doms(t.Mark, i, compared),
                [(t.DisplaySchedule, t.ScheduleBefore), (t.Priority, t.PriorityBefore), (t.WatchdogDisplay, t.WatchdogBefore), (t.CallsSummary, t.CallsBefore)])).ToList(),
            []);
    }

    /// <summary>The fragment for a device, compared or not.</summary>
    public static string Fragment(DeviceCatalog catalog, bool compared = false, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var connectors = catalog.KeyedConnectors();
        return Table("devices", catalog.Name, DeviceNote, catalog.ChangeSummary, Source(sourcePath, catalog.Path),
            ["CONNECTOR", "ID", "PARAMETERS"],
            connectors.Select((kv, i) => new RowCells(kv.Value.DisplayName, kv.Value.Mark, Doms(kv.Value.Mark, i, compared),
                [(kv.Value.ConnectorId, kv.Value.ConnectorIdBefore), (kv.Value.ParameterCount, kv.Value.ParameterCountBefore)]) { Key = kv.Key }).ToList(),
            // The slots are the device's other rows on the desktop tree; a reviewer of the whole
            // device sees them here, under the connectors, as the caption promises.
            catalog.Slots.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => (s.Key, s.Value)).ToList());
    }

    /// <summary>The review anchors a library table's rows stand for.</summary>
    public static IReadOnlyList<RenderedAnchor> Anchors(LibraryCatalog catalog, bool compared, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Anchors(Source(sourcePath, catalog.Path), catalog.Manager, compared, catalog.References.Select(r =>
            (r.Name, r.Mark, AnchorMatcher.Print(r.Name, r.DisplayVersion, r.Namespace, r.Kind),
                AnchorMatcher.Print(r.Name, r.ResolutionBefore ?? r.DisplayVersion, r.NamespaceBefore ?? r.Namespace, r.KindBefore ?? r.Kind))).ToList());
    }

    /// <summary>The review anchors a task table's rows stand for.</summary>
    public static IReadOnlyList<RenderedAnchor> Anchors(TaskCatalog catalog, bool compared, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Anchors(Source(sourcePath, catalog.Path), catalog.Configuration, compared, catalog.Tasks.Select(t =>
            (t.Name, t.Mark, AnchorMatcher.Print(t.Name, t.DisplaySchedule, t.Priority, t.WatchdogDisplay, t.CallsSummary),
                AnchorMatcher.Print(t.Name, t.ScheduleBefore ?? t.DisplaySchedule, t.PriorityBefore ?? t.Priority, t.WatchdogBefore ?? t.WatchdogDisplay, t.CallsBefore ?? t.CallsSummary))).ToList());
    }

    /// <summary>The review anchors a device table's rows stand for.</summary>
    public static IReadOnlyList<RenderedAnchor> Anchors(DeviceCatalog catalog, bool compared, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Anchors(Source(sourcePath, catalog.Path), catalog.Name, compared, catalog.KeyedConnectors().Select(kv =>
            (kv.Key, kv.Value.Mark, AnchorMatcher.Print(kv.Key, kv.Value.ConnectorId, kv.Value.ParameterCount),
                AnchorMatcher.Print(kv.Key, kv.Value.ConnectorIdBefore ?? kv.Value.ConnectorId, kv.Value.ParameterCountBefore ?? kv.Value.ParameterCount))).ToList());
    }

    private static string Source(string sourcePath, string own) => sourcePath.Length > 0 ? sourcePath : own;

    private static IReadOnlyList<RenderedAnchor> Anchors(
        string sourcePath, string container, bool compared, IReadOnlyList<(string Key, RowMark Mark, string Now, string Was)> rows)
    {
        var anchors = new List<RenderedAnchor>();
        for (var i = 0; i < rows.Count; i++)
        {
            var (key, mark, now, was) = rows[i];
            var (newDom, oldDom) = Doms(mark, i, compared);
            if (newDom is not null)
            {
                anchors.Add(new RenderedAnchor(newDom, "row", sourcePath, ReviewSide.New, container, key, now, null));
            }

            if (oldDom is not null)
            {
                // The older side shows what the row was: the "before" of each field that moved.
                anchors.Add(new RenderedAnchor(oldDom, "row", sourcePath, ReviewSide.Old, container, key, was, null));
            }
        }

        return anchors;
    }

    /// <summary>The anchor ids of one row on each side, or null where it does not exist on that side.</summary>
    private static (string? New, string? Old) Doms(RowMark mark, int index, bool compared)
    {
        var at = index.ToString(CultureInfo.InvariantCulture);
        return (mark.IsRemoved ? null : "r-new-" + at, compared && !mark.IsAdded ? "r-old-" + at : null);
    }

    /// <summary>One row: its name, its mark, its anchors, and the cells after the name with each one's old value when it moved.</summary>
    private sealed record RowCells(string Name, RowMark Mark, (string? New, string? Old) Doms, IReadOnlyList<(string Value, string? Before)> Cells)
    {
        /// <summary>The row's key for <c>data-key</c>; the name unless the catalog keys rows otherwise.</summary>
        public string Key { get; init; } = Name;
    }

    private static string Table(
        string kind, string title, string note, string summary, string sourcePath,
        IReadOnlyList<string> headers, IReadOnlyList<RowCells> rows, IReadOnlyList<(string Slot, string Value)> slots)
    {
        var hidden = 0;
        var head = new StringBuilder();
        head.Append("<div class=\"ct-head\"><span class=\"ct-title\">");
        hidden += CodeHtml.AppendLine(head, title, SyntaxLanguage.PlainText);
        head.Append("</span></div>\n<p class=\"ct-note\">").Append(CodeHtml.E(note)).Append("</p>\n");
        if (summary.Length > 0)
        {
            head.Append("<p class=\"ct-summary\">");
            hidden += CodeHtml.AppendLine(head, summary, SyntaxLanguage.PlainText);
            head.Append("</p>\n");
        }

        var body = new StringBuilder(rows.Count * 160 + 256);
        body.Append("<table class=\"ct-table\">\n<thead><tr><th class=\"ct-mark\"></th>");
        foreach (var header in headers)
        {
            body.Append("<th>").Append(CodeHtml.E(header)).Append("</th>");
        }

        body.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            hidden += Row(body, row);
        }

        if (slots.Count > 0)
        {
            body.Append("<tr class=\"ct-section\"><td colspan=\"").Append((headers.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\">Empty slots</td></tr>\n");
            foreach (var (slot, value) in slots)
            {
                body.Append("<tr class=\"ct-row ct-slot\" data-key=\"").Append(CodeHtml.A(slot)).Append("\"><td class=\"ct-mark\"></td><td class=\"ct-name\">");
                hidden += CodeHtml.AppendLine(body, slot, SyntaxLanguage.PlainText);
                body.Append("</td><td class=\"ct-cell\" colspan=\"").Append((headers.Count - 1).ToString(CultureInfo.InvariantCulture)).Append("\">");
                hidden += CodeHtml.AppendLine(body, value, SyntaxLanguage.PlainText);
                body.Append("</td></tr>\n");
            }
        }

        body.Append("</tbody>\n</table>\n");

        var html = new StringBuilder(head.Length + body.Length + 256);
        html.Append("<div class=\"ct-page\" data-kind=\"").Append(kind)
            .Append("\" data-src-path=\"").Append(CodeHtml.A(sourcePath))
            .Append("\" data-hidden-chars=\"").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append("\">\n")
            .Append(head);
        CodeHtml.AppendNotice(html, hidden);
        html.Append(body).Append("</div>\n");
        return html.ToString();
    }

    private static int Row(StringBuilder html, RowCells row)
    {
        var change = row.Mark.Change switch
        {
            MemberChange.Added => "added",
            MemberChange.Modified => "modified",
            MemberChange.Removed => "removed",
            _ => "none",
        };

        html.Append("<tr class=\"ct-row").Append(row.Mark.Change == MemberChange.None ? "" : " ct-" + change)
            .Append("\" data-key=\"").Append(CodeHtml.A(row.Key))
            .Append("\" data-change=\"").Append(change).Append('"')
            .Append(" data-anchors=\"").Append(string.Join(' ', new[] { row.Doms.New, row.Doms.Old }.Where(d => d is not null))).Append('"');
        if (row.Mark.Was.Length > 0)
        {
            // What the row said before, on hover, as the desktop's tooltip has it.
            html.Append(" title=\"Was: ").Append(CodeHtml.A(row.Mark.Was)).Append('"');
        }

        html.Append("><td class=\"ct-mark\">").Append(row.Mark.Glyph).Append("</td><td class=\"ct-name\">");
        var hidden = CodeHtml.AppendLine(html, row.Name, SyntaxLanguage.PlainText);
        html.Append("</td>");
        foreach (var (value, before) in row.Cells)
        {
            html.Append("<td class=\"ct-cell\">");
            hidden += Cell(html, value, before);
            html.Append("</td>");
        }

        html.Append("</tr>\n");
        return hidden;
    }

    /// <summary>A cell: the value, or old then new when it moved.</summary>
    private static int Cell(StringBuilder html, string value, string? before)
    {
        var hidden = 0;
        if (before is not null)
        {
            html.Append("<del class=\"ct-was\">");
            hidden += CodeHtml.AppendLine(html, before, SyntaxLanguage.PlainText);
            html.Append("</del> <ins class=\"ct-now\">");
            hidden += CodeHtml.AppendLine(html, value, SyntaxLanguage.PlainText);
            html.Append("</ins>");
            return hidden;
        }

        return CodeHtml.AppendLine(html, value, SyntaxLanguage.PlainText);
    }
}
