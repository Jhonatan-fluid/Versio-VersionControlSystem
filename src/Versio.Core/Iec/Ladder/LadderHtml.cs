using System.Globalization;
using System.Net;
using System.Text;

namespace Versio.Core.Iec.Ladder;

/// <summary>
/// A compared block as an HTML fragment of SVG networks, old beside new.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why SVG in HTML, written here.</b> Two places draw a ladder: this app's pane, through
/// WebView2, and the Outpost React page. Both are browsers. One renderer, written once in Core
/// and tested here, means the two cannot drift. React embeds the fragment and styles it with
/// <see cref="LadderPageAssets.Css"/>, the stylesheet the app uses. A TypeScript renderer fed
/// with JSON was rejected: it would put a second toolchain into the desktop build and move the
/// drawing away from the tests that pin it.
/// </para>
/// <para>
/// <b>Everything from the file is escaped.</b> Titles, comments and tag names are text the
/// owner's colleagues typed into the IDE. The rule is the one <c>MarkdownHtml</c> keeps: no text
/// from a file ever reaches the page as markup.
/// </para>
/// <para>
/// <b>It reads without script.</b> Each network is its own <c>&lt;svg&gt;</c> with a viewBox,
/// scaled to its column by CSS alone, never below 60% (then it scrolls sideways). The script in
/// <see cref="LadderPageAssets.Script"/> only adds navigation, the "changes only" toggle and
/// synced scrolling, and its controls are hidden until it runs, so a page without it shows no
/// buttons that do nothing.
/// </para>
/// </remarks>
public enum LadderView
{
    /// <summary>
    /// One rung per network, both versions on it: added and removed elements where they stand,
    /// modified ones with what they were. The default (the owner, 2026-10-05).
    /// </summary>
    Merged,

    /// <summary>The committed version on the left, the working one on the right.</summary>
    SideBySide,
}

public static class LadderHtml
{
    /// <summary>The fragment for a compared block.</summary>
    public static string Fragment(LadderComparison comparison, LadderStyle style, LadderView view = LadderView.Merged)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(style);

        // Side by side only when asked and when there is something to put beside: an unchanged
        // block, or one never committed, is one column, and two identical columns would only
        // halve its width.
        var twoColumns = view == LadderView.SideBySide && comparison.HasOldSide && comparison.HasChanges;
        var html = new StringBuilder();
        html.Append("<div class=\"ld-page\" data-block=\"").Append(E(comparison.BlockName))
            .Append("\" data-view=\"").Append(twoColumns ? "side-by-side" : "merged")
            .Append("\" data-changes-only=\"").Append(comparison.HasOldSide && comparison.HasChanges ? "true" : "false")
            .Append("\">\n");

        Summary(html, comparison, style);

        html.Append("<div class=\"ld-rows ").Append(twoColumns ? "ld-two" : "ld-one").Append("\">\n");
        foreach (var row in comparison.Rows)
        {
            if (twoColumns)
            {
                Row(html, row, style, twoColumns: true);
            }
            else
            {
                MergedRow(html, row, style);
            }
        }

        html.Append("</div>\n</div>\n");
        return html.ToString();
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ChangeName(LadderChange change) => change switch
    {
        LadderChange.MovedAndChanged => "moved-changed",
        LadderChange.MovedAway => "moved-away",
        _ => change.ToString().ToLowerInvariant(),
    };

    /// <summary>The id a row is jumped to by: its network's number on the side it is drawn by.</summary>
    private static string RowId(LadderRow row) => row.New is { } n
        ? $"ld-new-{n.Network.Number}"
        : $"ld-old-{row.Old!.Network.Number}";

    private static void Summary(StringBuilder html, LadderComparison comparison, LadderStyle style)
    {
        html.Append("<nav class=\"ld-summary\">");
        var unit = style.UnitName.ToLowerInvariant();
        if (!comparison.HasOldSide)
        {
            html.Append("<span class=\"ld-note\">No committed copy to compare with: every ")
                .Append(E(unit)).Append(" is new.</span>");
        }
        else if (!comparison.HasChanges)
        {
            html.Append("<span class=\"ld-note\">No changes since the last commit.</span>");
        }
        else
        {
            var counts = new (int Count, string Label, string Css, Func<LadderRow, bool> First)[]
            {
                (comparison.ChangedCount, "changed", "changed", r => r.Change is LadderChange.Changed or LadderChange.MovedAndChanged),
                (comparison.AddedCount, "added", "added", r => r.Change is LadderChange.Added or LadderChange.Replaced),
                (comparison.RemovedCount, "removed", "removed", r => r.Change is LadderChange.Removed or LadderChange.Replaced),
                (comparison.MovedCount, "moved", "moved", r => r.Change is LadderChange.Moved or LadderChange.MovedAndChanged),
            };

            var first = true;
            foreach (var (count, label, css, firstRow) in counts.Where(c => c.Count > 0))
            {
                if (!first)
                {
                    html.Append("<span class=\"ld-sep\"> · </span>");
                }

                first = false;
                var target = comparison.Rows.First(firstRow);
                html.Append("<a class=\"ld-count ld-c-").Append(css).Append("\" data-jump=\"").Append(RowId(target)).Append("\">")
                    .Append(count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(label).Append("</a>");
            }
        }

        html.Append("<span class=\"ld-tools\">")
            .Append("<button type=\"button\" data-action=\"prev\" title=\"Previous change (Shift+F7)\">&#9650;</button>")
            .Append("<button type=\"button\" data-action=\"next\" title=\"Next change (F7)\">&#9660;</button>")
            .Append("<label><input type=\"checkbox\" data-action=\"changes-only\"> Changes only</label>")
            .Append("</span></nav>\n");
    }

    private static void Row(StringBuilder html, LadderRow row, LadderStyle style, bool twoColumns)
    {
        var change = ChangeName(row.Change);
        if (row.Change == LadderChange.MovedAway)
        {
            var old = row.Old!.Network.Number;
            html.Append("<section class=\"ld-row ld-stub\" id=\"").Append(RowId(row))
                .Append("\" data-change=\"").Append(change).Append("\"><a class=\"ld-stub-text\" data-jump=\"ld-new-")
                .Append(row.MovedTo?.ToString(CultureInfo.InvariantCulture)).Append("\">&#8597; ")
                .Append(E(style.UnitName)).Append(' ').Append(old.ToString(CultureInfo.InvariantCulture))
                .Append(" moved to ").Append(E(style.UnitName.ToLowerInvariant())).Append(' ')
                .Append(row.MovedTo?.ToString(CultureInfo.InvariantCulture)).Append("</a></section>\n");
            return;
        }

        html.Append("<section class=\"ld-row ld-c-").Append(change).Append("\" id=\"").Append(RowId(row))
            .Append("\" data-change=\"").Append(change).Append("\">\n");

        if (twoColumns)
        {
            Side(html, row, row.Old, isOld: true, style);
            Side(html, row, row.New, isOld: false, style);
        }
        else
        {
            Side(html, row, row.New ?? row.Old, isOld: row.New is null, style);
        }

        html.Append("</section>\n");
    }

    /// <summary>
    /// One row of the merged view: a changed network drawn once with both versions on its rung,
    /// anything else as the one version it has.
    /// </summary>
    /// <remarks>
    /// A replaced network (removed, and an unrelated one written in its place) is two rungs in
    /// one row, the removed one above the added one: merging two unrelated rungs into one would
    /// draw a rung that never existed. The same holds for a changed network with no
    /// series/parallel shape on either side, which <see cref="LadderMerge"/> refuses to merge.
    /// </remarks>
    private static void MergedRow(StringBuilder html, LadderRow row, LadderStyle style)
    {
        var edited = row.Change is LadderChange.Changed or LadderChange.MovedAndChanged;
        var merged = edited && row is { Old: { } o, New: { } n } ? LadderMerge.Merge(o.Network, n.Network) : null;
        var stacked = row.Change == LadderChange.Replaced || (edited && merged is null);
        if (merged is null && !stacked)
        {
            Row(html, row, style, twoColumns: false);
            return;
        }

        var change = ChangeName(row.Change);
        html.Append("<section class=\"ld-row ld-c-").Append(change).Append("\" id=\"").Append(RowId(row))
            .Append("\" data-change=\"").Append(change).Append("\">\n");
        if (merged is not null)
        {
            Side(html, row, row.New, isOld: false, style, merged);
        }
        else
        {
            Side(html, row, row.Old, isOld: true, style);
            Side(html, row, row.New, isOld: false, style);
        }

        html.Append("</section>\n");
    }

    private static void Side(
        StringBuilder html, LadderRow row, LadderSide? side, bool isOld, LadderStyle style, LadderMergedNetwork? merged = null)
    {
        var which = isOld ? "ld-old" : "ld-new";
        if (side is null)
        {
            // The hatched gap keeps the other side's network level with its row.
            html.Append("<div class=\"ld-side ").Append(which).Append(" ld-gap\" aria-hidden=\"true\"></div>\n");
            return;
        }

        var network = side.Network;
        var (badge, css) = Badge(row, isOld, style);
        html.Append("<div class=\"ld-side ").Append(which);
        if (css is not null)
        {
            html.Append(" ld-c-").Append(css);
        }

        // The title and the comment are marked apart: a comment edited under an unchanged title
        // must not highlight the title, which then reads as renamed.
        var other = isOld ? row.New : row.Old;
        var titleChanged = row.HeaderChanged && other is not null && other.Network.Title != network.Title;
        var commentChanged = row.HeaderChanged && other is not null && other.Network.Comment != network.Comment;

        html.Append("\">\n<header class=\"ld-head\">")
            .Append("<span class=\"ld-num\">").Append(E(style.UnitName)).Append(' ')
            .Append(network.Number.ToString(CultureInfo.InvariantCulture)).Append("</span>");

        // Numbers are positional, so a network that kept its content can still have changed
        // its number. The new side says what it was.
        if (!isOld && row.Old is { } old && old.Network.Number != network.Number)
        {
            html.Append("<span class=\"ld-was\">was ").Append(old.Network.Number.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        }

        if (network.Title.Length > 0)
        {
            html.Append("<span class=\"ld-title").Append(titleChanged ? " ld-text-changed" : "").Append("\">")
                .Append(E(network.Title)).Append("</span>");
        }

        // Merged, there is no other side to read the old title from, so it is said here.
        if (merged is not null && titleChanged)
        {
            html.Append("<span class=\"ld-was\">was ")
                .Append(E(other!.Network.Title.Length > 0 ? other.Network.Title : "untitled")).Append("</span>");
        }

        if (badge is not null)
        {
            html.Append("<span class=\"ld-badge\">").Append(E(badge)).Append("</span>");
        }

        html.Append("</header>\n");

        var shape = LadderStructure.Of(network);
        if (shape.Labels.Count > 0)
        {
            html.Append("<div class=\"ld-labels\">")
                .AppendJoin(" ", shape.Labels.Select(l => "<span class=\"ld-label\">" + E(l.Name) + ":</span>"))
                .Append("</div>\n");
        }

        if (network.Comment.Length > 0)
        {
            html.Append("<p class=\"ld-comment").Append(commentChanged ? " ld-text-changed" : "").Append("\">")
                .Append(E(network.Comment)).Append("</p>\n");
        }

        if (merged is not null && commentChanged && other!.Network.Comment.Length > 0)
        {
            html.Append("<p class=\"ld-comment ld-was-text\">").Append(E(other.Network.Comment)).Append("</p>\n");
        }

        var drawing = merged is null ? LadderLayout.Lay(network, style) : LadderLayout.LayMerged(merged, style);
        if (drawing.FallbackReason is { } reason)
        {
            html.Append("<p class=\"ld-fallback\">Placed by power-flow order rather than as rungs: ")
                .Append(E(reason)).Append(".</p>\n");
        }

        Svg(html, drawing, merged?.Marks ?? side.Marks, network.Number, style);
        html.Append("</div>\n");
    }

    private static (string? Text, string? Css) Badge(LadderRow row, bool isOld, LadderStyle style)
    {
        var from = row.Old?.Network.Number.ToString(CultureInfo.InvariantCulture);
        var unit = style.UnitName.ToLowerInvariant();
        return row.Change switch
        {
            LadderChange.Changed => ("~ Changed", "changed"),
            LadderChange.Added => ("+ Added", "added"),
            LadderChange.Removed => ("− Removed", "removed"),
            LadderChange.Replaced when isOld => ("− Removed", "removed"),
            LadderChange.Replaced => ("+ Added", "added"),
            LadderChange.Moved when isOld => ("↕ Moved", "moved"),
            LadderChange.Moved => ($"↕ Moved from {unit} {from}", "moved"),
            LadderChange.MovedAndChanged when isOld => ("↕~ Moved and changed", "moved-changed"),
            LadderChange.MovedAndChanged => ($"↕~ Moved from {unit} {from} and changed", "moved-changed"),
            _ => (null, null),
        };
    }

    private static void Svg(
        StringBuilder html, LadderDrawing drawing, IReadOnlyDictionary<string, LdMark> marks, int number, LadderStyle style)
    {
        html.Append("<div class=\"ld-scroll\"><svg class=\"ld-net\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
            .Append(F(drawing.Width)).Append(' ').Append(F(drawing.Height))
            .Append("\" width=\"").Append(F(drawing.Width)).Append("\" height=\"").Append(F(drawing.Height))
            .Append("\" style=\"--w:").Append(F(drawing.Width)).Append("px\" role=\"img\" aria-label=\"")
            .Append(E(style.UnitName)).Append(' ').Append(number.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-net=\"").Append(number.ToString(CultureInfo.InvariantCulture)).Append("\">\n");

        foreach (var group in drawing.Groups)
        {
            var mark = group.Key.Length > 0 && marks.TryGetValue(group.Key, out var m) ? m : LdMark.Same;
            html.Append("<g class=\"ld-el ld-k-").Append(group.Kind);
            if (mark != LdMark.Same)
            {
                html.Append(" ld-m-").Append(mark.ToString().ToLowerInvariant());
            }

            html.Append('"');
            if (group.Key.Length > 0)
            {
                html.Append(" data-key=\"").Append(E(group.Key)).Append("\" data-change=\"")
                    .Append(mark.ToString().ToLowerInvariant()).Append('"');
            }

            html.Append('>');
            if (mark != LdMark.Same)
            {
                // Behind the element, so it reads as a highlight; dashed for removed, so added
                // and removed differ in grayscale too.
                var b = group.Bounds;
                html.Append("<rect class=\"ld-mark\" x=\"").Append(F(b.X + 1)).Append("\" y=\"").Append(F(b.Y + 1))
                    .Append("\" width=\"").Append(F(b.W - 2)).Append("\" height=\"").Append(F(b.H - 2))
                    .Append("\" rx=\"4\"/>");
            }

            foreach (var item in group.Items)
            {
                Primitive(html, item);
            }

            html.Append("</g>\n");
        }

        html.Append("</svg></div>\n");
    }

    private static void Primitive(StringBuilder html, LdPrimitive item)
    {
        switch (item)
        {
            case LdLine l:
                html.Append("<line x1=\"").Append(F(l.X1)).Append("\" y1=\"").Append(F(l.Y1))
                    .Append("\" x2=\"").Append(F(l.X2)).Append("\" y2=\"").Append(F(l.Y2)).Append("\"/>");
                break;
            case LdFrame f:
                html.Append("<rect class=\"ld-frame").Append(f.Dashed ? " ld-dashed" : "").Append("\" x=\"")
                    .Append(F(f.Rect.X)).Append("\" y=\"").Append(F(f.Rect.Y))
                    .Append("\" width=\"").Append(F(f.Rect.W)).Append("\" height=\"").Append(F(f.Rect.H)).Append("\"/>");
                break;
            case LdCircle c:
                html.Append("<circle class=\"ld-neg\" cx=\"").Append(F(c.X)).Append("\" cy=\"").Append(F(c.Y))
                    .Append("\" r=\"").Append(F(c.R)).Append("\"/>");
                break;
            case LdPath p:
                html.Append("<path d=\"").Append(E(p.Data)).Append("\"/>");
                break;
            case LdText t:
                html.Append("<text class=\"ld-t-").Append(t.Role).Append("\" x=\"").Append(F(t.X))
                    .Append("\" y=\"").Append(F(t.Y)).Append("\" text-anchor=\"")
                    .Append(t.Align switch { LdAlign.Start => "start", LdAlign.Middle => "middle", _ => "end" })
                    .Append("\">");
                if (t.Full != t.Text)
                {
                    html.Append("<title>").Append(E(t.Full)).Append("</title>");
                }

                html.Append(E(t.Text)).Append("</text>");
                break;
        }
    }
}
