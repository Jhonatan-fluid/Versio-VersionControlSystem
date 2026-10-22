using Versio.Core.Text;

namespace Versio.Core.Iec;

/// <summary>One row of a source file laid out for reading rather than diffing.</summary>
public abstract record SourceRow;

/// <summary>A line of the file, numbered the way the IDE numbers it.</summary>
/// <param name="Number">
/// The line's number within its own section when the file has sections, and the file's
/// line number when it has none. See <see cref="StSourceLayout"/> for why those differ.
/// </param>
/// <param name="FileLine">
/// Where the line sits in the file, from 1. Also what keeps two rows apart: a blank line 13
/// in the declaration and a blank line 13 in the implementation are otherwise the same
/// record, and a WPF list holding two equal items selects both when either is clicked —
/// and a copy pastes both.
/// </param>
public sealed record SourceCodeLine(int Number, string Text, int FileLine) : SourceRow
{
    /// <summary>Whether this line is new since the last commit. Set by <see cref="StSourceLayout.Mark"/>.</summary>
    public SourceChange Change { get; init; }

    /// <summary>
    /// For an unchanged line of a comparison, where it sat in the committed file, from 1; null
    /// otherwise. Set by <see cref="StSourceLayout.Mark"/>: a review comment on the older side of an
    /// unchanged line names this line, not <see cref="FileLine"/> (docs/web/PLAN.md, C3).
    /// </summary>
    public int? OldFileLine { get; init; }
}

/// <summary>What happened to a drawn line since the last commit.</summary>
public enum SourceChange
{
    None,
    Added,
}

/// <summary>
/// A line the last commit had and the working file does not, drawn where it stood.
/// </summary>
/// <param name="Text">The line as it was.</param>
/// <param name="OldFileLine">Where it sat in the committed file, from 1.</param>
/// <remarks>
/// Kept in the rows, where it stood, so side by side can face it with its replacement and the web
/// viewer can draw it struck. The desktop's unified Rendered view no longer draws it (the owner,
/// 2026-10-22: deleted lines belong in side by side); it marks the line now standing in its place
/// instead, because a deletion that left nothing on the tab would read as an unchanged file.
/// </remarks>
public sealed record SourceRemovedLine(string Text, int OldFileLine) : SourceRow;

/// <summary>What one side of a side-by-side row shows.</summary>
public enum SourceSide
{
    Same,
    Removed,
    Added,
}

/// <summary>One cell of a side-by-side source row: a line of one version, numbered in that version.</summary>
/// <param name="FileLine">
/// Where the line sits in its own version's file, from 1, or zero when unknown. <paramref name="Number"/>
/// is the IDE's section-relative number and restarts at every divider, so it cannot say which file
/// line a review comment belongs on; this can.
/// </param>
public sealed record SourceSplitCell(int Number, string Text, SourceSide Kind, int FileLine = 0);

/// <summary>
/// One row of source drawn side by side: the committed version on the left, the working one on
/// the right, or a divider across both.
/// </summary>
/// <param name="Left">The committed line, or null where the working file added one.</param>
/// <param name="Right">The working line, or null where the commit had one the file no longer does.</param>
/// <param name="Divider">A Declaration / Implementation divider spanning both sides, alone on its row.</param>
/// <param name="Fold">Unchanged lines folded away on both sides, alone on its row. See <see cref="StSourceLayout.Fold(IReadOnlyList{SourceSplitRow}, int)"/>.</param>
public sealed record SourceSplitRow(SourceSplitCell? Left, SourceSplitCell? Right, SourceDivider? Divider = null, SourceFold? Fold = null);

/// <summary>A run of unchanged lines left out of a comparison, drawn where they stood.</summary>
/// <param name="Hidden">How many rows it stands for.</param>
/// <param name="At">
/// Where the run began among the unfolded rows. Not drawn; it keeps two folds of the same length
/// apart, for the reason <see cref="SourceCodeLine.FileLine"/> gives — equal records in one WPF
/// list select together.
/// </param>
public sealed record SourceFold(int Hidden, int At) : SourceRow;

/// <summary>A drawn boundary between the declaration and the implementation.</summary>
/// <param name="Label">What the reader is told the section below is.</param>
/// <param name="FileText">
/// The file line this divider stands in for, or null when it stands in for none. A copy
/// that spans the divider pastes this, so the excerpt is still the file.
/// </param>
public sealed record SourceDivider(string Label, string? FileText) : SourceRow;

/// <summary>
/// Where a source file's parts are, by zero-based line, when the file says so in its own syntax
/// rather than with Versio's delimiter.
/// </summary>
/// <param name="HeaderEnd">
/// The first line after a header the vendor writes for itself (an object id, say), which the IDE
/// does not show. Zero when there is none.
/// </param>
/// <param name="DeclarationEnd">The first line after the declaration.</param>
/// <param name="BodyEnd">
/// The first line after the code body, or null when the object has no body (a type, a global
/// variable list). Lines from here on (a closing keyword alone on its line) are not drawn, as
/// the IDE does not draw them.
/// </param>
/// <remarks>
/// A vendor whose own files hold declaration and body with no divider between them (CODESYS
/// File-Based Storage does) is the only one who can say where the split is. The format answers
/// with this, and <see cref="StSourceLayout.Lay(string?, SourceSections?)"/> draws it the way it
/// draws Versio's own delimiter.
/// </remarks>
public sealed record SourceSections(int HeaderEnd, int DeclarationEnd, int? BodyEnd);

/// <summary>
/// Lays a <see cref="StTextCodec"/> payload out as the two panes the IDE shows: the
/// declaration, and the implementation numbered from 1 again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the numbering restarts.</b> Every IEC editor this project drives shows the
/// declaration and the implementation as two panes, each counting from 1, and its compiler
/// messages quote those numbers. The .st mirror joins both halves into one file, so its own
/// line numbers run on through the delimiter: <c>IF NOT Settings.Latch</c> in the fixture's
/// <c>FB_Alarm</c> was line 44 in Versio and line 16 in the IDE, and a reader handed a
/// compile error could not find the line it named.
/// </para>
/// <para>
/// <b>The numbers are <see cref="StTextCodec.Split"/>'s, not an approximation of them.</b>
/// Split trims each half with the payload trim set before the IDE ever sees it, so the
/// whitespace-only lines at either edge of a section are not lines of the PLC's source at
/// all — they are the codec's own framing, the blank either side of the delimiter that
/// <see cref="StTextCodec.Join"/> writes. They are left out rather than drawn unnumbered: the
/// divider already separates the halves, and a blank row with no number beside it reads as
/// a rendering fault. The delimiter is found the way Split finds it — the first occurrence —
/// and only drawn as a divider when it is the whole of its line. A hand-edited file with
/// code in front of it on the same line is split mid-line by the codec, and no row layout
/// can show that honestly, so that file falls back to plain file numbering instead of a
/// divider sitting in the wrong place.
/// </para>
/// <para>
/// A file with no delimiter — a DUT, a GVL, anything that is not ST at all — is numbered as
/// the file, which for a declaration-only payload is also the IDE's numbering, since
/// <see cref="StTextCodec.DeclarationOnly"/> writes no leading blank lines.
/// </para>
/// </remarks>
public static class StSourceLayout
{
    public const string DeclarationLabel = "Declaration";
    public const string ImplementationLabel = "Implementation";

    public static IReadOnlyList<SourceRow> Lay(string? content)
    {
        var lines = SplitLines(content);

        var delimiter = DelimiterLine(lines);
        if (delimiter < 0)
        {
            return Plain(lines);
        }

        var rows = new List<SourceRow>(lines.Count + 2)
        {
            new SourceDivider(DeclarationLabel, FileText: null),
        };
        AddSection(rows, lines, 0, delimiter);
        rows.Add(new SourceDivider(ImplementationLabel, lines[delimiter]));
        AddSection(rows, lines, delimiter + 1, lines.Count);
        return rows;
    }

    /// <summary>
    /// The file's lines, without the phantom one a trailing newline would add. A file
    /// ending in a newline has as many lines as it has newlines; splitting on them yields one
    /// more, and the old view numbered that empty tail as a line of its own.
    /// </summary>
    /// <summary>
    /// A file laid out by the sections its vendor's format found, or as <see cref="Lay(string?)"/>
    /// does when there are none.
    /// </summary>
    /// <remarks>
    /// The same rows a Versio <c>.st</c> gets: a Declaration divider, the declaration numbered
    /// from 1, an Implementation divider, and the body numbered from 1 again, with blank edges
    /// left out. The header and anything after the body are not drawn; the Raw tab still has them.
    /// An object with no body is drawn as its declaration alone, with no divider, which is how a
    /// type with no delimiter has always been drawn here.
    /// </remarks>
    public static IReadOnlyList<SourceRow> Lay(string? content, SourceSections? sections)
    {
        if (sections is null)
        {
            return Lay(content);
        }

        var lines = SplitLines(content);
        var header = Math.Clamp(sections.HeaderEnd, 0, lines.Count);
        var declarationEnd = Math.Clamp(sections.DeclarationEnd, header, lines.Count);

        var rows = new List<SourceRow>(lines.Count + 2);
        if (sections.BodyEnd is not { } end)
        {
            AddSection(rows, lines, header, declarationEnd);
            return rows;
        }

        var bodyEnd = Math.Clamp(end, declarationEnd, lines.Count);
        rows.Add(new SourceDivider(DeclarationLabel, FileText: null));
        AddSection(rows, lines, header, declarationEnd);
        rows.Add(new SourceDivider(ImplementationLabel, FileText: null));
        AddSection(rows, lines, declarationEnd, bodyEnd);
        return rows;
    }

    /// <summary>
    /// The laid-out rows with what changed since <paramref name="committed"/> marked: new lines as
    /// <see cref="SourceChange.Added"/>, and every removed line as a <see cref="SourceRemovedLine"/>
    /// in front of the first drawn line that followed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is of the whole files, by <see cref="SourceCodeLine.FileLine"/>, so it works
    /// for any layout: Versio's delimiter, a vendor's sections, plain numbering, or a code body
    /// shown under a member table. A removed line that stood in a part the layout does not draw (a
    /// vendor's header, a closing keyword) has no drawn line after it in that part, and is shown at
    /// the end rather than dropped: a reviewer told "modified" must find the modification.
    /// </para>
    /// <para>
    /// A null or empty <paramref name="committed"/> is a new file: every drawn line is added.
    /// </para>
    /// <para>
    /// <paramref name="hideRemovalsBeforeOldLine"/> is for the one layout whose rows are the
    /// code body alone, under a member table: a removal from the old file's declaration has no
    /// drawn line of its own part to stand before, so the catch-all above surfaced it at the
    /// top of the BODY — a struck <c>VAR_INPUT</c> leading a method's code (seen on a real
    /// library, 2026-10-14) — while the table above was already saying what the declaration
    /// lost. The caller passes where the old file's body began; removals from above that line
    /// belong to the table and are left out here. Zero, the default, hides nothing, which is
    /// right for every layout that draws the declaration itself.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SourceRow> Mark(
        IReadOnlyList<SourceRow> rows, string? committed, string? current,
        int hideRemovalsBeforeOldLine = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var oldLines = string.IsNullOrEmpty(committed) ? [] : SplitLines(committed);
        var newLines = SplitLines(current);
        var steps = Diff.LineDiff.Compare(oldLines, newLines);

        var added = new HashSet<int>();
        var sameOld = new Dictionary<int, int>();
        var removedBefore = new SortedDictionary<int, List<SourceRemovedLine>>();
        var nextNew = 0;
        var pending = new List<SourceRemovedLine>();
        foreach (var step in steps)
        {
            switch (step.Op)
            {
                case Diff.LineOp.Removed:
                    if (step.OldIndex >= hideRemovalsBeforeOldLine)
                    {
                        pending.Add(new SourceRemovedLine(oldLines[step.OldIndex], step.OldIndex + 1));
                    }

                    continue;
                case Diff.LineOp.Added:
                    added.Add(step.NewIndex);
                    break;
                case Diff.LineOp.Same:
                    sameOld[step.NewIndex] = step.OldIndex;
                    break;
            }

            nextNew = step.NewIndex + 1;
            if (pending.Count > 0)
            {
                removedBefore[step.NewIndex] = pending;
                pending = [];
            }
        }

        if (pending.Count > 0)
        {
            removedBefore[Math.Max(nextNew, newLines.Count)] = pending;
        }

        var marked = new List<SourceRow>(rows.Count + 4);
        foreach (var row in rows)
        {
            if (row is SourceCodeLine line)
            {
                var index = line.FileLine - 1;
                foreach (var key in removedBefore.Keys.Where(k => k <= index).ToList())
                {
                    marked.AddRange(removedBefore[key]);
                    removedBefore.Remove(key);
                }

                marked.Add(added.Contains(index) ? line with { Change = SourceChange.Added }
                    : sameOld.TryGetValue(index, out var was) ? line with { OldFileLine = was + 1 }
                    : line);
                continue;
            }

            marked.Add(row);
        }

        foreach (var group in removedBefore.Values)
        {
            marked.AddRange(group);
        }

        return marked;
    }

    /// <summary>
    /// Marked rows (<see cref="Mark"/>) as side-by-side pairs: removed lines facing the lines added
    /// in their place, unchanged lines level on both sides, dividers across both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner, 2026-10-10: "side by side not working on st". Raw had two columns and the
    /// ladder had two columns; the Rendered source drew its marks in one column whatever the box
    /// said.
    /// </para>
    /// <para>
    /// <b>Each side numbered as its own version.</b> The working side keeps the numbers the layout
    /// gave it. The committed side counts its own lines (unchanged and removed) from 1 again at
    /// every divider, as the layout numbers each section, so a removed line reads as the line it
    /// was in the IDE.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SourceSplitRow> SideBySide(IReadOnlyList<SourceRow> marked)
    {
        ArgumentNullException.ThrowIfNull(marked);

        var result = new List<SourceSplitRow>(marked.Count);
        var removed = new List<SourceSplitCell>();
        var added = new List<SourceSplitCell>();
        var oldNumber = 0;

        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                result.Add(new SourceSplitRow(i < removed.Count ? removed[i] : null, i < added.Count ? added[i] : null));
            }

            removed.Clear();
            added.Clear();
        }

        foreach (var row in marked)
        {
            switch (row)
            {
                case SourceDivider divider:
                    Flush();
                    oldNumber = 0;
                    result.Add(new SourceSplitRow(null, null, divider));
                    break;
                case SourceRemovedLine gone:
                    if (added.Count > 0)
                    {
                        Flush();
                    }

                    removed.Add(new SourceSplitCell(++oldNumber, gone.Text, SourceSide.Removed, gone.OldFileLine));
                    break;
                case SourceCodeLine { Change: SourceChange.Added } line:
                    added.Add(new SourceSplitCell(line.Number, line.Text, SourceSide.Added, line.FileLine));
                    break;
                case SourceCodeLine line:
                    Flush();
                    result.Add(new SourceSplitRow(
                        new SourceSplitCell(++oldNumber, line.Text, SourceSide.Same, line.OldFileLine ?? 0),
                        new SourceSplitCell(line.Number, line.Text, SourceSide.Same, line.FileLine)));
                    break;
            }
        }

        Flush();
        return result;
    }

    /// <summary>
    /// Marked rows with every run of unchanged lines further than <paramref name="context"/>
    /// from a change folded into one <see cref="SourceFold"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the pane's <i>Show full file</i> box means on the Rendered tab when it is clear. The
    /// box re-read git with more context, which only Raw draws; the Rendered tab always drew the
    /// whole file, so on the tab that opens first the box did nothing at all (the owner,
    /// 2026-10-22). Folded here, both tabs answer the box the same way: git's three lines either
    /// side of a change, or everything.
    /// </para>
    /// <para>
    /// Dividers are never folded, so a reader always sees which half a change is in. A run of
    /// one line is drawn rather than folded: a fold row costs the same space and hides the line.
    /// Rows with nothing marked come back as they are — a file nobody is comparing is not folded.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SourceRow> Fold(IReadOnlyList<SourceRow> marked, int context = Git.GitDiff.DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(marked);
        return Fold(marked,
            r => r is SourceRemovedLine or SourceCodeLine { Change: SourceChange.Added },
            r => r is SourceDivider,
            (hidden, at) => new SourceFold(hidden, at),
            context);
    }

    /// <summary>Side-by-side rows (<see cref="SideBySide"/>) folded as the unified ones are.</summary>
    /// <remarks>
    /// Folded after pairing rather than before: each side numbers itself by counting its own
    /// lines, and lines folded away before the count would renumber every one below them.
    /// </remarks>
    public static IReadOnlyList<SourceSplitRow> Fold(IReadOnlyList<SourceSplitRow> pairs, int context = Git.GitDiff.DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return Fold(pairs,
            p => p.Divider is null && p.Fold is null
                 && (p.Left?.Kind is not SourceSide.Same || p.Right?.Kind is not SourceSide.Same),
            p => p.Divider is not null,
            (hidden, at) => new SourceSplitRow(null, null, Fold: new SourceFold(hidden, at)),
            context);
    }

    private static IReadOnlyList<T> Fold<T>(
        IReadOnlyList<T> rows, Func<T, bool> isChange, Func<T, bool> isKept, Func<int, int, T> fold, int context)
    {
        var shown = new bool[rows.Count];
        var any = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (isKept(rows[i]))
            {
                shown[i] = true;
            }

            if (!isChange(rows[i]))
            {
                continue;
            }

            any = true;
            for (var j = Math.Max(0, i - context); j <= Math.Min(rows.Count - 1, i + context); j++)
            {
                shown[j] = true;
            }
        }

        if (!any)
        {
            return rows;
        }

        var result = new List<T>(rows.Count);
        for (var i = 0; i < rows.Count;)
        {
            if (shown[i])
            {
                result.Add(rows[i++]);
                continue;
            }

            var start = i;
            while (i < rows.Count && !shown[i])
            {
                i++;
            }

            if (i - start == 1)
            {
                result.Add(rows[start]);
            }
            else
            {
                result.Add(fold(i - start, start));
            }
        }

        return result;
    }

    private static List<string> SplitLines(string? content)
    {
        var lines = (content ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>The line holding Split's delimiter when it is the whole line, else -1.</summary>
    private static int DelimiterLine(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Contains(StTextCodec.ImplementationDelimiter, StringComparison.Ordinal))
            {
                continue;
            }

            // The FIRST occurrence decides, as it does in Split. A later whole-line delimiter
            // is not the separator when an earlier one already cut the file.
            return PayloadWhitespace.Trim(lines[i]) == StTextCodec.ImplementationDelimiter
                ? i
                : -1;
        }

        return -1;
    }

    /// <summary>
    /// Lines [start, end) of a file as a block's body: blank edges dropped, the indent every
    /// non-blank line shares removed, numbered from 1.
    /// </summary>
    /// <remarks>
    /// For the code pane under a block's interface table. The owner, 2026-10-05: the
    /// <c>BEGIN</c> / <c>END_FUNCTION_BLOCK</c> marks are not necessary, the tab on the left
    /// should go, and the lines should restart rather than begin at 22. The shared prefix is
    /// removed character for character - a tab is not taken for four spaces - so a body indented
    /// with tabs and nested with spaces keeps its nesting exactly. <see cref="SourceCodeLine.FileLine"/>
    /// still holds the line's place in the file, for anything that needs to find it there.
    /// </remarks>
    public static IReadOnlyList<SourceRow> Body(string? content, int start, int end)
    {
        var lines = SplitLines(content);
        start = Math.Clamp(start, 0, lines.Count);
        end = Math.Clamp(end, start, lines.Count);

        while (start < end && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        string? shared = null;
        for (var i = start; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var indent = lines[i][..(lines[i].Length - lines[i].TrimStart(' ', '\t').Length)];
            shared = shared is null ? indent : CommonPrefix(shared, indent);
        }

        var cut = shared?.Length ?? 0;
        var rows = new List<SourceRow>(end - start);
        for (var i = start; i < end; i++)
        {
            var text = lines[i].Length >= cut ? lines[i][cut..] : lines[i].TrimStart(' ', '\t');
            rows.Add(new SourceCodeLine(i - start + 1, text, i + 1));
        }

        return rows;
    }

    private static string CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n])
        {
            n++;
        }

        return a[..n];
    }

    private static List<SourceRow> Plain(List<string> lines)
    {
        var rows = new List<SourceRow>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            rows.Add(new SourceCodeLine(i + 1, lines[i], i + 1));
        }

        return rows;
    }

    /// <summary>Lines [from, to) with Split's trimmed edges dropped, numbered from 1.</summary>
    private static void AddSection(List<SourceRow> rows, List<string> lines, int from, int to)
    {
        while (from < to && IsBlank(lines[from]))
        {
            from++;
        }

        while (to > from && IsBlank(lines[to - 1]))
        {
            to--;
        }

        for (var i = from; i < to; i++)
        {
            rows.Add(new SourceCodeLine(i - from + 1, lines[i], i + 1));
        }
    }

    private static bool IsBlank(string line) => PayloadWhitespace.Trim(line).Length == 0;
}
