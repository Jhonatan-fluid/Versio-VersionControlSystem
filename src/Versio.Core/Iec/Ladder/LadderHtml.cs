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

/// <summary>
/// What the summary line says when there is nothing to count: the host's words, because only the
/// host knows what the comparison is against.
/// </summary>
/// <param name="Unchanged">Said when nothing changed; empty says nothing.</param>
/// <param name="NoOldSide">Said when there is no older copy; <c>{0}</c> is the vendor's word for a network.</param>
/// <remarks>
/// The desktop always compares with the last commit, and <see cref="Desktop"/> is its wording,
/// unchanged. The web viewer shows one commit, or two the reader chose, and "since the last
/// commit" was wrong there in both cases (seen in the browser on 2026-10-11).
/// </remarks>
public sealed record LadderWording(string Unchanged, string NoOldSide)
{
    public static readonly LadderWording Desktop = new(
        "No changes since the last commit.",
        "No committed copy to compare with: every {0} is new.");
}

public static class LadderHtml
{
    /// <summary>The fragment for a compared block.</summary>
    /// <param name="sourcePath">The file the networks were parsed from, carried as <c>data-src-path</c> for review anchors.</param>
    public static string Fragment(LadderComparison comparison, LadderStyle style, LadderView view = LadderView.Merged, LadderWording? wording = null, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(style);
        wording ??= LadderWording.Desktop;

        // Side by side only when asked and when there is something to put beside: an unchanged
        // block, or one never committed, is one column, and two identical columns would only
        // halve its width.
        var twoColumns = view == LadderView.SideBySide && comparison.HasOldSide && comparison.HasChanges;
        var html = new StringBuilder();
        html.Append("<div class=\"ld-page\" data-src-path=\"").Append(E(sourcePath)).Append("\" data-block=\"").Append(E(comparison.BlockName))
            .Append("\" data-view=\"").Append(twoColumns ? "side-by-side" : "merged")
            .Append("\" data-changes-only=\"").Append(comparison.HasOldSide && comparison.HasChanges ? "true" : "false")
            .Append("\">\n");

        Summary(html, comparison, style, wording);

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

    /// <summary>
    /// The review anchors a comparison's networks stand for: each network on each side it exists on
    /// (docs/web/PLAN.md, *Pull request review*, C3). A network's key is its title, or its number
    /// when it has none — numbers are positional and shift on a delete, which is what the
    /// fingerprint is for.
    /// </summary>
    /// <remarks>
    /// A moved-away stub stands for nothing of its own: its network is the older side of the row it
    /// moved to, and listing it twice would make every comment on it ambiguous.
    /// </remarks>
    public static IReadOnlyList<Review.RenderedAnchor> Anchors(LadderComparison comparison, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var anchors = new List<Review.RenderedAnchor>();
        foreach (var row in comparison.Rows.Where(r => r.Change != LadderChange.MovedAway))
        {
            if (row.New is { } n)
            {
                anchors.Add(Anchor(n.Network, Review.ReviewSide.New, comparison.BlockName, sourcePath));
            }

            if (row.Old is { } o && comparison.HasOldSide)
            {
                anchors.Add(Anchor(o.Network, Review.ReviewSide.Old, comparison.BlockName, sourcePath));
            }
        }

        return anchors;
    }

    private static Review.RenderedAnchor Anchor(LadderNetwork network, Review.ReviewSide side, string block, string sourcePath) =>
        new(Dom(network, side), "net", sourcePath, side, block,
            network.Title.Length > 0 ? "t:" + network.Title : "#" + network.Number.ToString(CultureInfo.InvariantCulture),
            Review.AnchorMatcher.Print(LadderStructure.Fingerprint(network)), network.Lines);

    private static string Dom(LadderNetwork network, Review.ReviewSide side) =>
        (side == Review.ReviewSide.New ? "n-new-" : "n-old-") + network.Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The anchors a drawn side stands for: its own network, and the older one too when that is not
    /// drawn on a side of its own (an unchanged or merged network is drawn once, for both).
    /// </summary>
    private static string Doms(LadderRow row, bool isOld, bool oldDrawnApart)
    {
        if (row.Change == LadderChange.MovedAway)
        {
            return "";
        }

        if (isOld)
        {
            return row.Old is { } o ? Dom(o.Network, Review.ReviewSide.Old) : "";
        }

        var doms = row.New is { } n ? Dom(n.Network, Review.ReviewSide.New) : "";
        return !oldDrawnApart && row.Old is { } old ? (doms + " " + Dom(old.Network, Review.ReviewSide.Old)).Trim() : doms;
    }

    /// <summary>The id a row is jumped to by: its network's number on the side it is drawn by.</summary>
    private static string RowId(LadderRow row) => row.New is { } n
        ? $"ld-new-{n.Network.Number}"
        : $"ld-old-{row.Old!.Network.Number}";

    private static void Summary(StringBuilder html, LadderComparison comparison, LadderStyle style, LadderWording wording)
    {
        html.Append("<nav class=\"ld-summary\">");
        var unit = style.UnitName.ToLowerInvariant();
        if (!comparison.HasOldSide)
        {
            html.Append("<span class=\"ld-note\">")
                .Append(E(string.Format(CultureInfo.InvariantCulture, wording.NoOldSide, unit))).Append("</span>");
        }
        else if (!comparison.HasChanges)
        {
            if (wording.Unchanged.Length > 0)
            {
                html.Append("<span class=\"ld-note\">").Append(E(wording.Unchanged)).Append("</span>");
            }
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
            Side(html, row, row.Old, isOld: true, style, oldDrawnApart: true);
            Side(html, row, row.New, isOld: false, style, oldDrawnApart: true);
        }
        else
        {
            Side(html, row, row.New ?? row.Old, isOld: row.New is null, style, oldDrawnApart: false);
        }

        html.Append("</section>\n");
    }

    /// <summary>A changed network's merged drawing, and the marks that go on it.</summary>
    private sealed record MergedView(LadderDrawing Drawing, IReadOnlyDictionary<string, LdMark> Marks);

    /// <summary>
    /// The merged drawing of an edited network, or null when it is drawn as two stacked versions.
    /// </summary>
    /// <remarks>
    /// Ladder merges both versions onto one rung (<see cref="LadderMerge.Merge"/>), its removed
    /// elements left in place. A function block diagram draws the new network once, its
    /// modified boxes saying what they were, and only when nothing was removed
    /// (<see cref="LadderMerge.FunctionBlockWas"/>).
    /// </remarks>
    private static MergedView? Merged(
        LadderNetwork old, LadderNetwork @new, IReadOnlyDictionary<string, LdMark> newMarks, LadderStyle style)
    {
        if (@new.Language == LdLanguage.FunctionBlock)
        {
            return LadderMerge.FunctionBlockWas(old, @new) is { } was
                ? new MergedView(LadderLayout.Lay(@new, style, was), newMarks)
                : null;
        }

        return LadderMerge.Merge(old, @new) is { } rung
            ? new MergedView(LadderLayout.LayMerged(rung, style), rung.Marks)
            : null;
    }

    /// <summary>
    /// One row of the merged view: a changed network drawn once with both versions on its rung,
    /// anything else as the one version it has.
    /// </summary>
    /// <remarks>
    /// A replaced network (removed, and an unrelated one written in its place) is two rungs in
    /// one row, the removed one above the added one: merging two unrelated rungs into one would
    /// draw a rung that never existed. The same holds for a changed ladder network with no
    /// series/parallel shape on either side, which <see cref="LadderMerge"/> refuses to merge, and
    /// for a function block diagram that lost an element.
    /// </remarks>
    private static void MergedRow(StringBuilder html, LadderRow row, LadderStyle style)
    {
        var edited = row.Change is LadderChange.Changed or LadderChange.MovedAndChanged;
        var merged = edited && row is { Old: { } o, New: { } n } ? Merged(o.Network, n.Network, row.New.Marks, style) : null;
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
            Side(html, row, row.New, isOld: false, style, oldDrawnApart: false, merged);
        }
        else
        {
            Side(html, row, row.Old, isOld: true, style, oldDrawnApart: true);
            Side(html, row, row.New, isOld: false, style, oldDrawnApart: true);
        }

        html.Append("</section>\n");
    }

    private static void Side(
        StringBuilder html, LadderRow row, LadderSide? side, bool isOld, LadderStyle style, bool oldDrawnApart, MergedView? merged = null)
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

        html.Append("\" data-anchors=\"").Append(Doms(row, isOld, oldDrawnApart)).Append("\">\n<header class=\"ld-head\">")
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

        var drawing = merged?.Drawing ?? LadderLayout.Lay(network, style);
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
