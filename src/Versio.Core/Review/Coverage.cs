using System.Text.RegularExpressions;
using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Projects;

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
/// <b>Claim only what is proven.</b> A drawing that shows a member as modified may still hide an
/// attribute changed on the same line, so being inside a drawn element proves nothing. A changed
/// line counts as represented only when the renderer can name it: a source row drawn as added, a
/// removed row drawn struck, or a declaration line whose every token is a field the member table
/// displays for a row it shows as changed. Everything else is a gap, and the page says "may not
/// show — check Raw" for it.
/// </para>
/// <para>
/// <b>Evidence is per renderer, and absence of evidence is a gap.</b> A ladder, or a table read
/// from XML, has no field-level evidence yet: every changed line of its file is reported, so its
/// view stays labelled partial — the rule is that a renderer leaves "partial" only when its tests
/// prove it can.
/// </para>
/// </remarks>
public static partial class Coverage
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
            var (removed, added) = ChangedLines(old, @new);
            changed += removed.Count + added.Count;
            var providers = evidence.TryGetValue(path, out var list) ? list : [];
            var reason = providers.Count == 0 ? noEvidence(path) : "not-shown";
            gaps.AddRange(Runs(path, ReviewSide.Old, removed.Where(l => !providers.Any(p => p(ReviewSide.Old, l))), reason));
            gaps.AddRange(Runs(path, ReviewSide.New, added.Where(l => !providers.Any(p => p(ReviewSide.New, l))), reason));
        }

        return new ReviewCoverage(changed, gaps);
    }

    /// <summary>The removed lines (1-based, in the old text) and added lines (in the new) between two versions.</summary>
    public static (IReadOnlyList<int> Removed, IReadOnlyList<int> Added) ChangedLines(string? old, string? @new)
    {
        var steps = LineDiff.Compare(Lines(old), Lines(@new));
        return (steps.Where(s => s.Op == LineOp.Removed).Select(s => s.OldIndex + 1).ToList(),
            steps.Where(s => s.Op == LineOp.Added).Select(s => s.NewIndex + 1).ToList());
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
    /// when it lies in one row's lines on that side, that row is drawn as changed, and nothing on
    /// the line is left over once the row's displayed fields and the declaration syntax around them
    /// are taken away.
    /// </summary>
    /// <remarks>
    /// The leftover test is what keeps this honest. A declaration line of Structured Text is the
    /// name, the type, the value and the comment, all displayed; a line of XML is mostly markup and
    /// attributes the table does not show, so it is never vouched for — the view stays partial
    /// until a parser reports XML fields by location, which is the next renderer to earn it.
    /// </remarks>
    public static Evidence Table(MemberTable compared, string? oldText, string? newText)
    {
        ArgumentNullException.ThrowIfNull(compared);
        var oldLines = Lines(oldText);
        var newLines = Lines(newText);
        var rows = compared.Members.Where(r => !r.IsSectionHeading).ToList();
        return (side, line) =>
        {
            var text = side == ReviewSide.New ? At(newLines, line) : At(oldLines, line);

            // The object's own attributes, drawn as chips over the table: a changed one vouches
            // for its line on the same leftover test, so the line holds nothing but attributes.
            var chips = compared.Attributes.Where(a => (side == ReviewSide.New ? a.Lines : a.WasLines) is { } at && at.Contains(line)
                                                       && (side == ReviewSide.New ? !a.IsRemoved : !a.IsAdded)).ToList();
            if (chips.Count > 0)
            {
                return text is not null && chips.Any(a => a.Change != MemberChange.None)
                    && Tokens(text).All(chips.SelectMany(a => Tokens(Pragma(a, side))).ToHashSet(StringComparer.Ordinal).Contains);
            }

            var holders = rows.Where(r => (side == ReviewSide.New ? r.Lines : r.WasLines) is { } span && span.Contains(line)).ToList();
            if (holders.Count != 1 || holders[0].Change == MemberChange.None)
            {
                return false;
            }

            var row = holders[0];
            if (text is null)
            {
                return false;
            }

            var shown = side == ReviewSide.New
                ? new[] { row.Name, row.Datatype, row.StartValue, row.ExternalAccess, row.Comment }
                : [row.Name, row.DatatypeBefore ?? row.Datatype, row.StartValueBefore ?? row.StartValue, row.ExternalAccessBefore ?? row.ExternalAccess, row.CommentBefore ?? row.Comment];
            // The row's attribute lines are in its span, and its chips draw them on the side
            // they exist on.
            var pragmas = row.Attributes.Where(a => side == ReviewSide.New ? !a.IsRemoved : !a.IsAdded).Select(a => Pragma(a, side));
            // Whole tokens, never substrings: taking "INT" out of "INTERNAL" would leave a line
            // with a hidden change looking accounted for.
            var displayed = shown.Concat(pragmas).SelectMany(Tokens).ToHashSet(StringComparer.Ordinal);
            return Tokens(text).All(displayed.Contains);
        };
    }

    /// <summary>
    /// An attribute as the pragma that writes it, on one side, so its tokens are the ones a line
    /// spelled the usual way splits into. A pragma spelled any other way leaves tokens over and
    /// is not vouched for, which is the safe way round.
    /// </summary>
    private static string Pragma(MemberAttribute a, ReviewSide side)
    {
        var value = side == ReviewSide.New ? a.Value : a.OlderValue;
        return value.Length == 0 ? $"{{attribute '{a.Name}'}}" : $"{{attribute '{a.Name}' := '{value}'}}";
    }

    /// <summary>A line's words, split at spacing and at the declaration syntax around fields: punctuation and comment marks.</summary>
    private static IEnumerable<string> Tokens(string? value) =>
        string.IsNullOrEmpty(value) ? [] : Syntax().Split(value).Where(t => t.Length > 0);

    [GeneratedRegex(@"[\s:=;,()\[\]]+|//|\(\*|\*\)")]
    private static partial Regex Syntax();

    private static string? At(IReadOnlyList<string> lines, int line) => line >= 1 && line <= lines.Count ? lines[line - 1] : null;

    private static List<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n').ToList();

    /// <summary>Consecutive lines folded into ranges, so a ten-line edit is one gap, not ten.</summary>
    private static IEnumerable<CoverageGap> Runs(string path, ReviewSide side, IEnumerable<int> lines, string reason)
    {
        int? start = null, end = null;
        foreach (var line in lines.Order())
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
