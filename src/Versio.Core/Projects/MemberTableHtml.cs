using System.Globalization;
using System.Text;
using Versio.Core.Diff;
using Versio.Core.Review;

namespace Versio.Core.Projects;

/// <summary>
/// A <see cref="MemberTable"/> (a data type, a block interface, a variable list) as an HTML
/// fragment: the desktop's member table, for a browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same columns, in the same order, with the same rules for leaving one out</b>:
/// NAME, TYPE (only when a row has a type), the value column under the name the IDE uses,
/// EXTERNAL ACCESS (only when a row has it), COMMENT, and the A / M / D marker. Those decisions
/// already live on <see cref="MemberTable"/>, so this draws them rather than making them again.
/// </para>
/// <para>
/// <b>A modified row shows old then new in exactly the cells that moved</b>, as the desktop
/// does (the owner, 2026-10-05): a reviewer sees what changed, not only that the row did. A
/// removed row stays, struck. A row whose referenced type changed is marked, because that is a
/// change to this block's meaning its own diff does not show.
/// </para>
/// <para>
/// <b>Attributes are chips, as on the desktop</b>: the object's in a strip under the title, a
/// member's under its name, each marked added, changed or removed. A declaration change the
/// table cannot draw at all is said in one line over it (<see cref="MemberTable.UnshownChange"/>).
/// </para>
/// <para>
/// <b>Every cell goes through <see cref="CodeHtml"/></b>: escaped, and a hidden character in a
/// name or a comment shown as a marker. A right-to-left override in a variable name misleads a
/// reviewer as much as one in code does.
/// </para>
/// <para>
/// <b>Each row carries <c>data-path</c>, the dotted member path</b>, as the contract a link or a
/// review comment attaches to, the way code rows carry <c>data-line</c>. A linked type carries
/// <c>data-link</c> with the object the host resolved; the host decides what a click opens.
/// Indentation is drawn with spans, not inline styles, so a strict content-security policy can
/// forbid style attributes.
/// </para>
/// <para>
/// <b>Each member row also carries <c>data-anchors</c></b>: the ids of the review anchors it stands
/// for, one per side it exists on (<see cref="Anchors"/>), and the page carries the layer the rows
/// were parsed from in <c>data-src-path</c>. One rule decides both (<see cref="Doms"/>), so the
/// anchor list and the markup cannot disagree about which row is which.
/// </para>
/// </remarks>
public static class MemberTableHtml
{
    /// <summary>The fragment for <paramref name="table"/>, compared or not.</summary>
    /// <param name="compared">A comparison: rows exist on the older side too, and say so in their anchors.</param>
    public static string Fragment(MemberTable table, bool compared = false)
    {
        ArgumentNullException.ThrowIfNull(table);

        var hidden = 0;
        var head = new StringBuilder();
        head.Append("<div class=\"mt-head\"><span class=\"mt-title\">");
        hidden += CodeHtml.AppendLine(head, table.Name, SyntaxLanguage.PlainText);
        head.Append("</span>");
        foreach (var caption in new[] { table.NumberCaption, table.InstanceCaption, table.MemoryLayoutCaption })
        {
            if (caption.Length > 0)
            {
                head.Append("<span class=\"mt-caption\">");
                hidden += CodeHtml.AppendLine(head, caption, SyntaxLanguage.PlainText);
                head.Append("</span>");
            }
        }

        head.Append("</div>\n<p class=\"mt-note\">").Append(CodeHtml.E(table.ReadOnlyNote)).Append("</p>\n");
        if (table.HasChangeSummary)
        {
            head.Append("<p class=\"mt-summary\">");
            hidden += CodeHtml.AppendLine(head, table.ChangeSummary, SyntaxLanguage.PlainText);
            head.Append("</p>\n");
        }

        if (table.HasUnshownChange)
        {
            head.Append("<p class=\"mt-unshown\" role=\"note\">");
            hidden += CodeHtml.AppendLine(head, table.UnshownChange, SyntaxLanguage.PlainText);
            head.Append("</p>\n");
        }

        if (table.HasAttributes)
        {
            head.Append("<div class=\"mt-attrs\"><span class=\"mt-attrs-label\">Attributes</span>");
            hidden += Chips(head, table.Attributes);
            head.Append("</div>\n");
        }

        var columns = 4 + (table.HasDatatype ? 1 : 0) + (table.HasExternalAccess ? 1 : 0);
        var body = new StringBuilder(table.Members.Count * 160);
        body.Append("<table class=\"mt-table\">\n<thead><tr><th class=\"mt-mark\"></th><th>")
            .Append(CodeHtml.E(table.NameHeader)).Append("</th>");
        if (table.HasDatatype)
        {
            body.Append("<th>TYPE</th>");
        }

        body.Append("<th>").Append(CodeHtml.E(table.ValueHeader)).Append("</th>");
        if (table.HasExternalAccess)
        {
            body.Append("<th>EXTERNAL ACCESS</th>");
        }

        body.Append("<th>COMMENT</th></tr></thead>\n<tbody>\n");
        for (var i = 0; i < table.Members.Count; i++)
        {
            var row = table.Members[i];
            hidden += row.IsSectionHeading ? Section(body, row, columns) : Row(body, row, table, Doms(row, i, compared));
        }

        body.Append("</tbody>\n</table>\n");

        var html = new StringBuilder(head.Length + body.Length + 256);
        html.Append("<div class=\"mt-page\" data-src-path=\"").Append(CodeHtml.A(table.SourcePath))
            .Append("\" data-kind=\"").Append(Kind(table))
            .Append("\" data-hidden-chars=\"").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append("\">\n")
            .Append(head);
        CodeHtml.AppendNotice(html, hidden);
        html.Append(body).Append("</div>\n");
        return html.ToString();
    }

    private static int Section(StringBuilder html, MemberRow row, int columns)
    {
        html.Append("<tr class=\"mt-section\"><td colspan=\"").Append(columns.ToString(CultureInfo.InvariantCulture)).Append("\">");
        var hidden = CodeHtml.AppendLine(html, row.Name, SyntaxLanguage.PlainText);
        html.Append("</td></tr>\n");
        return hidden;
    }

    /// <summary>
    /// The review anchors a table's rows stand for: a row on the newer side unless it was removed,
    /// and on the older side when the table is a comparison and the row is not new.
    /// </summary>
    public static IReadOnlyList<RenderedAnchor> Anchors(MemberTable table, bool compared)
    {
        ArgumentNullException.ThrowIfNull(table);
        var anchors = new List<RenderedAnchor>();
        for (var i = 0; i < table.Members.Count; i++)
        {
            var row = table.Members[i];
            if (row.IsSectionHeading)
            {
                continue;
            }

            var (newDom, oldDom) = Doms(row, i, compared);
            if (newDom is not null)
            {
                anchors.Add(new RenderedAnchor(newDom, "row", table.SourcePath, ReviewSide.New, table.Name, row.Path,
                    AnchorMatcher.Print(row.Section, row.Name, row.Datatype, row.StartValue, row.ExternalAccess, row.Comment), row.Lines));
            }

            if (oldDom is not null)
            {
                // The older side shows what the row was: the "before" of each field that moved.
                anchors.Add(new RenderedAnchor(oldDom, "row", table.SourcePath, ReviewSide.Old, table.Name, row.Path,
                    AnchorMatcher.Print(row.Section, row.Name, row.DatatypeBefore ?? row.Datatype, row.StartValueBefore ?? row.StartValue,
                        row.ExternalAccessBefore ?? row.ExternalAccess, row.CommentBefore ?? row.Comment), row.WasLines));
            }
        }

        return anchors;
    }

    /// <summary>The anchor ids of one row on each side, or null where it does not exist on that side.</summary>
    private static (string? New, string? Old) Doms(MemberRow row, int index, bool compared)
    {
        var at = index.ToString(CultureInfo.InvariantCulture);
        return (row.Change == MemberChange.Removed ? null : "r-new-" + at,
            compared && row.Change != MemberChange.Added ? "r-old-" + at : null);
    }

    private static int Row(StringBuilder html, MemberRow row, MemberTable table, (string? New, string? Old) doms)
    {
        var change = row.Change switch
        {
            MemberChange.Added => "added",
            MemberChange.Modified => "modified",
            MemberChange.Removed => "removed",
            _ => "none",
        };

        html.Append("<tr class=\"mt-row").Append(row.Change == MemberChange.None ? "" : " mt-" + change)
            .Append(row.ReferenceChanged ? " mt-ref-changed" : "")
            .Append("\" data-path=\"").Append(CodeHtml.A(row.Path))
            .Append("\" data-change=\"").Append(change).Append('"')
            .Append(" data-anchors=\"").Append(string.Join(' ', new[] { doms.New, doms.Old }.Where(d => d is not null))).Append('"');
        if (row.Was.Length > 0)
        {
            // What the row said before, on hover, as the desktop's tooltip has it. An attribute
            // cannot hold a marker element, so CodeHtml.A spells a hidden character as U+XXXX.
            html.Append(" title=\"Was: ").Append(CodeHtml.A(row.Was)).Append('"');
        }

        html.Append("><td class=\"mt-mark\">").Append(row.ChangeGlyph).Append("</td><td class=\"mt-name\">");
        for (var d = 0; d < Math.Max(0, row.Depth); d++)
        {
            html.Append("<span class=\"mt-indent\"></span>");
        }

        var hidden = CodeHtml.AppendLine(html, row.Name, SyntaxLanguage.PlainText);
        if (row.HasAttributes)
        {
            html.Append("<span class=\"mt-chips\">");
            hidden += Chips(html, row.Attributes);
            html.Append("</span>");
        }

        html.Append("</td>");

        if (table.HasDatatype)
        {
            html.Append("<td class=\"mt-type\">");
            hidden += row.LinkGuid is { } link
                ? Linked(html, row.Datatype, row.DatatypeBefore, link)
                : Cell(html, row.Datatype, row.DatatypeBefore);
            html.Append("</td>");
        }

        html.Append("<td class=\"mt-value\">");
        hidden += Cell(html, row.StartValue, row.StartValueBefore);
        html.Append("</td>");

        if (table.HasExternalAccess)
        {
            html.Append("<td class=\"mt-access\">");
            hidden += Cell(html, row.ExternalAccess, row.ExternalAccessBefore);
            html.Append("</td>");
        }

        html.Append("<td class=\"mt-comment\">");
        hidden += Cell(html, row.Comment, row.CommentBefore);
        html.Append("</td></tr>\n");
        return hidden;
    }

    /// <summary>A cell: the value, or old then new when it moved.</summary>
    private static int Cell(StringBuilder html, string value, string? before)
    {
        var hidden = 0;
        if (before is not null)
        {
            html.Append("<del class=\"mt-was\">");
            hidden += CodeHtml.AppendLine(html, before, SyntaxLanguage.PlainText);
            html.Append("</del> <ins class=\"mt-now\">");
            hidden += CodeHtml.AppendLine(html, value, SyntaxLanguage.PlainText);
            html.Append("</ins>");
            return hidden;
        }

        return CodeHtml.AppendLine(html, value, SyntaxLanguage.PlainText);
    }

    /// <summary>Attributes as chips: <c>name = value</c>, the change as a class and in words on hover.</summary>
    private static int Chips(StringBuilder html, IReadOnlyList<MemberAttribute> attributes)
    {
        var hidden = 0;
        foreach (var a in attributes)
        {
            var change = a.Change switch
            {
                MemberChange.Added => " mt-chip-added",
                MemberChange.Modified => " mt-chip-modified",
                MemberChange.Removed => " mt-chip-removed",
                _ => "",
            };
            html.Append("<span class=\"mt-chip").Append(change).Append("\" title=\"").Append(CodeHtml.A(a.ChangeLabel)).Append("\">");
            hidden += CodeHtml.AppendLine(html, a.Text, SyntaxLanguage.PlainText);
            if (a.IsModified)
            {
                // Old value after the new, struck, as the desktop chip draws it.
                html.Append(" <del class=\"mt-was\">");
                hidden += CodeHtml.AppendLine(html, a.OlderValue, SyntaxLanguage.PlainText);
                html.Append("</del>");
            }

            html.Append("</span>");
        }

        return hidden;
    }

    private static int Linked(StringBuilder html, string value, string? before, string link)
    {
        html.Append("<a class=\"mt-link\" data-link=\"").Append(CodeHtml.A(link)).Append("\">");
        var hidden = Cell(html, value, before);
        html.Append("</a>");
        return hidden;
    }

    private static string Kind(MemberTable table) => (table.IsDataType, table.IsInterface, table.IsVariableList) switch
    {
        (true, _, _) => "datatype",
        (_, true, _) => "interface",
        (_, _, true) => "variables",
        _ => "block",
    };
}
