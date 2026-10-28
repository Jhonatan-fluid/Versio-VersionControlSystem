using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Iec.Ladder;
using Versio.Core.Projects;
using Versio.Core.Text;

namespace Versio.Core.Review;

/// <summary>
/// Changed lines a drawing may not show, in one file, on one side: what a reviewer must read on
/// Raw before approving (docs/web/PLAN.md, *Pull request review*, C4).
/// </summary>
/// <param name="Reason">
/// not-drawn (the file is not part of this drawing), no-evidence (its renderer cannot say which
/// fields it shows), not-shown (a line the drawing does not name: an attribute, a pragma, spacing).
/// </param>
public sealed record CoverageGap(string Path, ReviewSide Side, int Start, int End, string Reason);

/// <summary>What a drawing can vouch for about a comparison.</summary>
/// <param name="Changed">How many changed lines the files of the drawing have, both sides together.</param>
public sealed record ReviewCoverage(int Changed, IReadOnlyList<CoverageGap> Gaps)
{
    /// <summary>Every changed line is one the drawing names as changed. The only state in which the page drops its "may be partial" warning.</summary>
    public bool Complete => Gaps.Count == 0;

    public static ReviewCoverage Empty { get; } = new(0, []);
}

/// <summary>
/// Which changed lines a drawing represents (docs/web/PLAN.md, *Pull request review*, C4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim only what the renderer drew, and say the rest once.</b> A changed line is represented
/// when a renderer can name it: a source row drawn as added or struck; a line inside a member
/// row the table draws as added, changed or removed; a changed attribute chip's line; a network
/// drawn on a side; or a line the parser declared <i>framing</i> — the declaration's own
/// keywords, the markup around and between the rows of an XML file, blank lines — which the
/// drawing has no cell for and which a reviewer would gain nothing from reading on Raw.
/// Everything else is a gap, and the page says "may not show — check Raw" for it.
/// </para>
/// <para>
/// <b>Why the rules loosened (2026-10-23).</b> The first version vouched for a declaration line
/// only when every token on it was a field the table displays, and for no XML line at all. On the
/// owner's real pull request (85 files, 2026-10-23) that made the banner
/// noise: a re-indented interface listed twelve "changed, but not shown" lines, a new 780-line
/// symbol set drawn as a table with every row marked A said "lines 1–780 not shown", and a method
/// whose header gained <c>PUBLIC</c> reported the same edit twice, as it was and as it is. Every
/// one of those was the rule being right about the letters and wrong about the drawing. The
/// rules now (each has a test and a killed mutation in <c>CoverageTests</c>):
/// <list type="number">
/// <item>A file entirely new or entirely deleted, drawn by any renderer with evidence, is complete:
/// every row is A (or D), and there is nothing a reviewer could be missing.</item>
/// <item>A row drawn as A / D / M vouches for every line it spans on that side, pragma chips
/// included; an unchanged row vouches for its lines when their text holds the same words as before
/// (re-aligned, a comment moved to the end of its line), because the cells it draws are the same cells.</item>
/// <item>The declaration's framing — the header, <c>VAR</c>, <c>END_VAR</c>, <c>END_TYPE</c> —
/// is vouched for by the table that draws the object (<see cref="MemberTable.Structural"/>).</item>
/// <item>A line edited in place is one gap, named as it is now, never once per side.</item>
/// <item>An XML table vouches for markup-only lines between its rows the same way (the parser
/// declares them; <see cref="Structural"/>), and keeps "not-shown" for a changed value the table does
/// not draw.</item>
/// <item>A ladder has evidence (<see cref="Ladder"/>): every network vouches for its span on the
/// sides it exists on, and only lines outside every network and outside the declared framing remain.</item>
/// </list>
/// What stays a gap, deliberately: a changed line under an unchanged row that is not spacing (a
/// pragma the table does not read), a comment line no row owns, a changed value in XML the table
/// has no column for, a <c>EXTENDS</c> added to a header, and anything a renderer did not draw.
/// </para>
/// <para>
/// <b>Evidence is per renderer, and absence of evidence is a gap.</b> A file drawn with no evidence
/// at all is reported whole, with the caller's reason — the rule is that a renderer leaves "partial"
/// only when its tests prove it can.
/// </para>
/// </remarks>
public static class Coverage
{
    /// <summary>What one renderer can vouch for in one file: a changed line on a side, shown or not.</summary>
    public delegate bool Evidence(ReviewSide side, int line);

    /// <summary>
    /// The gaps across a drawing's files: for each file, every changed line no evidence for that file
    /// vouches for; a file with no evidence at all is reported whole, with <paramref name="noEvidence"/>
    /// naming why.
    /// </summary>
    /// <param name="files">Each file of the object: its path and both versions' text (null where absent).</param>
    /// <param name="evidence">What each renderer can vouch for, by the path of the file it drew from.</param>
    /// <remarks>
    /// <para>
    /// <b>Rule 1, whole files.</b> A file with no older version (or no newer one) that has evidence is
    /// complete without reading a line: the renderer drew the whole object as added (or removed),
    /// so nothing it drew can be hiding a change. The symbol set case: 780 lines, every row A, and
    /// the old rule listed all 780 because XML lines are not fields.
    /// </para>
    /// <para>
    /// <b>Rule 4, once per edit.</b> The diff reports a line edited in place twice — the old text
    /// removed, the new text added — and the first version reported both as gaps, so one hidden edit
    /// read as two. A run of removed-then-added lines is one edit: when its newer side has a gap,
    /// that is the gap, and the older side's lines are not listed again. The older side is named
    /// only where it is all there is (a pure deletion, or a replacement whose newer lines are all
    /// vouched for).
    /// </para>
    /// </remarks>
    public static ReviewCoverage Check(
        IEnumerable<(string Path, string? Old, string? New)> files,
        IReadOnlyDictionary<string, IReadOnlyList<Evidence>> evidence,
        Func<string, string> noEvidence)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(noEvidence);

        var gaps = new List<CoverageGap>();
        var changed = 0;
        foreach (var (path, old, @new) in files)
        {
            var providers = evidence.TryGetValue(path, out var list) ? list : [];
            var reason = providers.Count == 0 ? noEvidence(path) : "not-shown";
            var oldGaps = new List<int>();
            var newGaps = new List<int>();
            foreach (var (removed, added) in Edits(old, @new))
            {
                changed += removed.Count + added.Count;
                if (providers.Count > 0 && (string.IsNullOrEmpty(old) || string.IsNullOrEmpty(@new)))
                {
                    continue;
                }

                var missingNew = added.Where(l => !providers.Any(p => p(ReviewSide.New, l))).ToList();
                newGaps.AddRange(missingNew);
                if (removed.Count > 0 && added.Count > 0 && missingNew.Count > 0)
                {
                    continue;
                }

                oldGaps.AddRange(removed.Where(l => !providers.Any(p => p(ReviewSide.Old, l))));
            }

            gaps.AddRange(Runs(path, ReviewSide.Old, oldGaps, reason));
            gaps.AddRange(Runs(path, ReviewSide.New, newGaps, reason));
        }

        return new ReviewCoverage(changed, gaps);
    }

    /// <summary>The removed lines (1-based, in the old text) and added lines (in the new) between two versions.</summary>
    public static (IReadOnlyList<int> Removed, IReadOnlyList<int> Added) ChangedLines(string? old, string? @new)
    {
        var edits = Edits(old, @new).ToList();
        return (edits.SelectMany(e => e.Removed).ToList(), edits.SelectMany(e => e.Added).ToList());
    }

    /// <summary>
    /// The changes between two versions as edits: each a run of removed and added lines with no
    /// unchanged line inside it, which is how a reader sees one change on Raw.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<int> Removed, IReadOnlyList<int> Added)> Edits(string? old, string? @new)
    {
        var removed = new List<int>();
        var added = new List<int>();
        foreach (var step in LineDiff.Compare(Lines(old), Lines(@new)))
        {
            switch (step.Op)
            {
                case LineOp.Removed:
                    removed.Add(step.OldIndex + 1);
                    continue;
                case LineOp.Added:
                    added.Add(step.NewIndex + 1);
                    continue;
            }

            if (removed.Count > 0 || added.Count > 0)
            {
                yield return (removed, added);
                removed = [];
                added = [];
            }
        }

        if (removed.Count > 0 || added.Count > 0)
        {
            yield return (removed, added);
        }
    }

    /// <summary>
    /// A drawn source layout's evidence: a new line is shown when it is drawn as added, an old one
    /// when it is drawn struck. Lines the layout leaves out (a vendor's header) are not.
    /// </summary>
    public static Evidence Source(IReadOnlyList<SourceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var added = rows.OfType<SourceCodeLine>().Where(r => r.Change == SourceChange.Added).Select(r => r.FileLine).ToHashSet();
        var removed = rows.OfType<SourceRemovedLine>().Select(r => r.OldFileLine).ToHashSet();
        return (side, line) => side == ReviewSide.New ? added.Contains(line) : removed.Contains(line);
    }

    /// <summary>
    /// A compared member table's evidence, for the text it was read from: a changed line is shown
    /// when a row drawn as changed spans it, when an unchanged row spans it and the row's text
    /// holds the same words as before, when a changed attribute chip is written on it, or when the
    /// parser declared it framing (<see cref="MemberTable.Structural"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The span is the evidence, since 2026-10-23.</b> The first version took the row's displayed
    /// fields away from the line and vouched only when nothing was left over, so that a pragma
    /// changed beside a shown value would stay a gap. The table draws pragmas as chips now, so a
    /// row's span holds nothing the table does not draw — and the token test had turned
    /// <c>STRING(GVL.LENGTH)</c> against itself, because a parenthesis splits a token the parser kept
    /// whole. A row marked A, D or M is the drawing saying "this changed"; that is what the line is
    /// vouched by.
    /// </para>
    /// <para>
    /// <b>The same words under an unchanged row.</b> The owner's FB_Alarm re-aligned twelve
    /// declarations: twelve changed lines, twelve rows reading unchanged, twelve warnings. The
    /// cells the table draws are the same cells, and the only thing a reviewer could find on Raw
    /// is the tab. Its GVL_Logger moved a comment from above <c>BATCH_CAPACITY</c> to the end of
    /// its line: the same comment cell, drawn the same. So an unchanged row vouches for its lines
    /// when its old and new text hold the same words — the row's cells are already known equal,
    /// so equal words mean nothing was added or taken away, only arranged — and for nothing else:
    /// a pragma the parser does not read, or a comment restyled from <c>//</c> to <c>(* *)</c>,
    /// changes the words and leaves the line a gap.
    /// </para>
    /// </remarks>
    public static Evidence Table(MemberTable compared, string? oldText, string? newText)
    {
        ArgumentNullException.ThrowIfNull(compared);
        var oldLines = Lines(oldText);
        var newLines = Lines(newText);
        var rows = compared.Members.Where(r => !r.IsSectionHeading).ToList();
        var framing = Structural(compared.StructuralBefore, compared.Structural);
        var modifiers = Modifiers(compared);

        bool SameWords(MemberRow row) =>
            row.Lines is { } now && row.WasLines is { } was
            && Words(Text(newLines, now)).SequenceEqual(Words(Text(oldLines, was)), StringComparer.Ordinal);

        // What the table itself says it does not draw, on each side. It overrides every rule
        // below: a row's span runs from its comment to its declaration, and a pragma or a
        // conditional line between the two is inside the span and drawn nowhere — vouched for by
        // the modified row, it was reported "complete" with the change hidden (review, 2026-10-24).
        var unshownNew = compared.Unshown.Select(u => u.Line).ToHashSet();
        var unshownOld = compared.UnshownBefore.Select(u => u.Line).ToHashSet();

        return (side, line) =>
        {
            if ((side == ReviewSide.New ? unshownNew : unshownOld).Contains(line))
            {
                return false;
            }

            var holders = rows.Where(r => (side == ReviewSide.New ? r.Lines : r.WasLines) is { } span && span.Contains(line)).ToList();
            if (holders.Any(r => r.Change != MemberChange.None || SameWords(r)))
            {
                return true;
            }

            // The object's own attributes, drawn as chips over the table: a chip drawn as changed
            // vouches for the line it is written on, on the side it exists on.
            var chips = compared.Attributes.Where(a => a.Change != MemberChange.None
                                                       && (side == ReviewSide.New ? a.Lines : a.WasLines) is { } at && at.Contains(line));
            return chips.Any() || modifiers(side, line) || framing(side, line);
        };
    }

    /// <summary>
    /// The header's modifiers, drawn as chips beside the title: one drawn as changed vouches for
    /// the header line on BOTH sides, because the chip is written on one side only (a gained
    /// <c>PUBLIC</c> has no line on the older side) while the header line is edited on both.
    /// </summary>
    /// <remarks>
    /// Needed where the header is not framing: a method with a return type has its header as its
    /// Return row, whose words differ across the sides and whose cells do not, so until 2026-10-28
    /// a gained <c>PUBLIC</c> was a gap there — and on a method without one it was framing, vouched
    /// for and not drawn. Every other edit to that line is some other rule's: the return type and
    /// the comment are the Return row's cells, the name is a row added and one removed, and an
    /// <c>EXTENDS</c> is in the unshown list, which refuses above.
    /// </remarks>
    private static Evidence Modifiers(MemberTable compared)
    {
        if (compared.Modifiers.All(m => m.Change == MemberChange.None))
        {
            return (_, _) => false;
        }

        return (side, line) => (side == ReviewSide.New ? compared.Header : compared.HeaderBefore) is { } header && header.Contains(line);
    }

    /// <summary>
    /// Framing a parser declared on each side: lines that are the file's own syntax around what the
    /// drawing shows — a declaration's keywords, the markup between an XML table's rows, blank lines —
    /// which no cell could show differently and which a reviewer sent to Raw would read for nothing.
    /// </summary>
    /// <remarks>
    /// The parser decides, never this class: which lines are framing is the format's knowledge
    /// (<see cref="MarkupLines"/> for XML, the declaration reader for Structured Text), and a line it
    /// did not declare stays a gap. That is the safe way round: a parser that declares nothing
    /// changes nothing.
    /// </remarks>
    public static Evidence Structural(IReadOnlyList<LineSpan> oldSpans, IReadOnlyList<LineSpan> newSpans)
    {
        ArgumentNullException.ThrowIfNull(oldSpans);
        ArgumentNullException.ThrowIfNull(newSpans);
        return (side, line) => (side == ReviewSide.New ? newSpans : oldSpans).Any(s => s.Contains(line));
    }

    /// <summary>
    /// A compared ladder's evidence: every network vouches for its span on each side it is drawn on,
    /// and the framing each version's parser declared (<see cref="LadderDocument.Structural"/>) is
    /// vouched for too. What remains a gap is a line outside every network that the parser did not
    /// declare: a setting the drawing has no element for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An unchanged network vouches for its span as well</b> — the owner's rule (2026-10-23).
    /// A network whose lines changed while its drawing did not is one whose vendor renumbered an
    /// element id or rewrote layout data: the vendor's bookkeeping, which a diagram cannot show and
    /// a reviewer cannot act on. A changed network is marked where it changed, so its span is the
    /// drawing saying so. <b>The cost, stated:</b> the model carries an element's operand, its
    /// kind and its instance, not everything the file holds for it — an operand's comment, its
    /// symbol comment, its address, a contact's attributes, a TIA part's flags — and an edit to one
    /// of those alone compares as Same and is vouched for by this rule. FINISH.md carries it with
    /// the element anchors, which need the same per-element parsing.
    /// </para>
    /// <para>
    /// Spans come from the networks the rows hold, so a moved network vouches for its old lines as
    /// well as its new ones, and a replaced row vouches for both networks on their own sides.
    /// </para>
    /// </remarks>
    public static Evidence Ladder(LadderComparison comparison, IReadOnlyList<LineSpan> oldStructural, IReadOnlyList<LineSpan> newStructural)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        // A placeholder network — one in a language the diagram does not draw (an IL network
        // in a ladder, an SCL compile unit in a LAD block) — holds no elements, so its fingerprint
        // is its title and its language and nothing of its text: two versions with different
        // instructions compare as Same. Its span is not vouched for; those lines stay gaps, which is
        // the honest answer for code the drawing shows as a box with a name (review, 2026-10-24).
        static bool Drawn(LadderNetwork network) => network.UnsupportedLanguage is null;
        var newSpans = comparison.Rows.Select(r => r.New?.Network).Where(n => n is not null && Drawn(n)).Select(n => n!.Lines).OfType<LineSpan>().ToList();
        var oldSpans = comparison.HasOldSide ? comparison.Rows.Select(r => r.Old?.Network).Where(n => n is not null && Drawn(n)).Select(n => n!.Lines).OfType<LineSpan>().ToList() : [];
        var framing = Structural(oldStructural, newStructural);
        return (side, line) => (side == ReviewSide.New ? newSpans : oldSpans).Any(s => s.Contains(line)) || framing(side, line);
    }

    /// <summary>The text of <paramref name="span"/>'s lines, joined; lines past the end are left out.</summary>
    private static string Text(IReadOnlyList<string> lines, LineSpan span) =>
        string.Join('\n', Enumerable.Range(span.Start, span.Length).Select(l => At(lines, l)).Where(t => t is not null));

    /// <summary>A text's words in sorted order: what two arrangements of the same declaration share.</summary>
    private static IEnumerable<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);

    private static string? At(IReadOnlyList<string> lines, int line) => line >= 1 && line <= lines.Count ? lines[line - 1] : null;

    /// <summary>
    /// The file's lines, without the phantom one a trailing newline would add: the layouts count
    /// lines the same way (<see cref="StSourceLayout"/>), and a newline gained at the end of a
    /// file is not a line anyone can read.
    /// </summary>
    private static List<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Consecutive lines folded into ranges, so a ten-line edit is one gap, not ten.</summary>
    private static IEnumerable<CoverageGap> Runs(string path, ReviewSide side, IEnumerable<int> lines, string reason)
    {
        int? start = null, end = null;
        foreach (var line in lines.Distinct().Order())
        {
            if (end is { } e && line == e + 1)
            {
                end = line;
                continue;
            }

            if (start is { } s)
            {
                yield return new CoverageGap(path, side, s, end!.Value, reason);
            }

            start = end = line;
        }

        if (start is { } last)
        {
            yield return new CoverageGap(path, side, last, end!.Value, reason);
        }
    }
}
