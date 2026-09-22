namespace Versio.Core.Diff;

/// <summary>What one line of a unified diff represents.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,

    /// <summary>An <c>@@ … @@</c> hunk header.</summary>
    HunkHeader,

    /// <summary>File headers, index lines, mode changes — shown dimmed.</summary>
    FileHeader,

    /// <summary>
    /// One file's heading: its path, what happened to it, and its own +/− counts. See
    /// <see cref="DiffFileTitle"/>.
    /// </summary>
    FileTitle,

    /// <summary>"\ No newline at end of file".</summary>
    NoNewline,
}

/// <summary>One line of a unified diff, with its number on each side.</summary>
public sealed record DiffLine
{
    public required DiffLineKind Kind { get; init; }
    public required string Text { get; init; }

    /// <summary>Line number in the old file, when the line exists there.</summary>
    public int? OldLine { get; init; }

    /// <summary>Line number in the new file, when the line exists there.</summary>
    public int? NewLine { get; init; }

    /// <summary>
    /// Which hunk this line belongs to, counting from zero, or -1 for a file header.
    /// </summary>
    /// <remarks>
    /// Carried on the line because the view renders lines and nothing else — a row that
    /// offers to stage a hunk has to know which one it is standing in, and asking the document
    /// to work it out by position would mean the answer depended on a search every time a
    /// container was recycled.
    /// </remarks>
    public int HunkIndex { get; init; } = -1;

    /// <summary>
    /// The part of this line that actually changed, when a changed pair could say.
    /// </summary>
    /// <remarks>
    /// Filled by <see cref="IntralineEmphasis.Annotate"/>, not by the parser: the parser's
    /// job is to repeat what git said, and which characters moved is a comparison git's
    /// unified output does not carry. Empty means "no emphasis", which is the ordinary
    /// state of context lines, unpaired lines, and pairs with nothing in common.
    /// </remarks>
    public IReadOnlyList<TextSpan> Emphasis { get; init; } = [];

    /// <summary>The file this heading introduces. Set only on a <see cref="DiffLineKind.FileTitle"/>.</summary>
    public DiffFileTitle? Title { get; init; }

    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        _ => " ",
    };
}

/// <summary>What a diff did to one file.</summary>
public enum DiffFileChange
{
    Modified,
    Added,
    Deleted,
    Renamed,
}

/// <summary>One file's heading inside a diff that may hold several.</summary>
/// <param name="Path">The file's path after the change — or before it, for a deletion.</param>
/// <param name="OldPath">Where a renamed file came from; null otherwise.</param>
/// <param name="Change">What happened to it.</param>
/// <param name="Added">Lines added to this file.</param>
/// <param name="Removed">Lines removed from this file.</param>
public sealed record DiffFileTitle(
    string Path, string? OldPath, DiffFileChange Change, int Added, int Removed);

/// <summary>A parsed unified diff, ready to render.</summary>
public sealed record DiffDocument
{
    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
    public int Added { get; init; }
    public int Removed { get; init; }
    public bool IsBinary { get; init; }

    /// <summary>How many hunks the diff contains, across every file in it.</summary>
    public int Hunks { get; init; }

    /// <summary>Every file's heading, in order. Empty for a diff with no <c>diff --git</c> line.</summary>
    public IReadOnlyList<DiffFileTitle> Files { get; init; } = [];

    public bool IsEmpty => Lines.Count == 0;

    public string Summary => IsBinary
        ? "Binary file — no textual diff"
        : $"+{Added} −{Removed}";
}

/// <summary>
/// Parses git's unified diff output into a structured model.
/// </summary>
/// <remarks>
/// <para>
/// Parsing git's own output rather than computing a second diff is deliberate: a separate
/// diff implementation could disagree with the one that produced the commit, and then the
/// view would be showing something git does not believe.
/// </para>
/// <para>
/// Line numbers are tracked from the hunk headers so the gutter can show both sides. That
/// is the difference between a diff you can discuss ("line 213") and one you can only
/// squint at.
/// </para>
/// <para>
/// <b>Each file's header block becomes one title line.</b> A commit's patch arrived as
/// <c>diff --git</c>, <c>index</c>, <c>---</c> and <c>+++</c> per file, dimmed but raw, so a
/// twelve-file commit read as one long run of text with the file boundaries the hardest thing
/// to find in it. Seen in the running window on 2026-09-21. Those lines say only which file,
/// and whether it was added, deleted or renamed — so they fold into a
/// <see cref="DiffLineKind.FileTitle"/> carrying exactly that, plus the file's own counts. A
/// mode change and a binary notice say something the title does not, and stay as dimmed
/// lines. A diff with no <c>diff --git</c> at all — a bare hunk — keeps the old handling.
/// </para>
/// <para>
/// This is the view's model only. Staging a hunk rebuilds its patch from git's raw text
/// (<see cref="HunkPatchBuilder"/>), never from these lines, so nothing git needs is lost.
/// </para>
/// </remarks>
public static class UnifiedDiffParser
{
    public static DiffDocument Parse(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            return new DiffDocument();
        }

        var lines = new List<DiffLine>();
        var added = 0;
        var removed = 0;
        var binary = false;
        var oldLine = 0;
        var newLine = 0;
        var hunk = -1;

        // The file being read, while inside a `diff --git` block: where its title sits in
        // `lines`, and what it has learned about the file so far. Finished (counts filled in)
        // when the next file starts, and at the end.
        var titleAt = -1;
        DiffFileTitle? title = null;
        var inHeader = false;
        var fileAdded = 0;
        var fileRemoved = 0;

        // Lines the open hunk still owes on each side, from its @@ header. While either is
        // above zero every line is content, whatever it starts with: a removed line reading
        // "-- x" arrives as "--- x", and was taken for a file header - dimmed, uncounted, and
        // out of step with the line numbers after it. The header check below runs only
        // between hunks.
        var oldLeft = 0;
        var newLeft = 0;
        var files = new List<DiffFileTitle>();

        void FinishFile()
        {
            if (titleAt < 0 || title is null)
            {
                return;
            }

            // Counted in plain integers and applied once here: a record copy per changed line
            // would allocate for every line of the 33,644-line visualisation diff the fixture
            // produces.
            var done = title with { Added = fileAdded, Removed = fileRemoved };
            lines[titleAt] = lines[titleAt] with { Text = done.Path, Title = done };
            files.Add(done);
            titleAt = -1;
            title = null;
            fileAdded = 0;
            fileRemoved = 0;
        }

        foreach (var raw in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FinishFile();
                var path = PathFromDiffGit(raw);
                title = new DiffFileTitle(path, null, DiffFileChange.Modified, 0, 0);
                titleAt = lines.Count;
                inHeader = true;
                lines.Add(new DiffLine { Kind = DiffLineKind.FileTitle, Text = path, Title = title });
                continue;
            }

            // Inside a file's header block - after its `diff --git`, before its first `@@` -
            // the lines the title already says are absorbed into it; the rest fall through to
            // be kept as dimmed header lines. Only there: a removed line reading "-- x" inside
            // a hunk is content, not a header.
            if (inHeader && title is not null)
            {
                var folded = title;
                if (Absorb(raw, ref folded))
                {
                    title = folded;
                    continue;
                }
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                inHeader = false;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldLine, newLine, oldLeft, newLeft) = ParseHunkHeader(raw);
                hunk++;
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.HunkHeader,
                    Text = raw,
                    HunkIndex = hunk,
                });
                continue;
            }

            if (oldLeft <= 0 && newLeft <= 0 && IsFileHeader(raw))
            {
                if (raw.StartsWith("Binary files", StringComparison.Ordinal) ||
                    raw.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    binary = true;
                }

                lines.Add(new DiffLine { Kind = DiffLineKind.FileHeader, Text = raw });
                continue;
            }

            if (raw.StartsWith('\\'))
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.NoNewline,
                    Text = raw,
                    HunkIndex = hunk,
                });
                continue;
            }

            switch (raw[0])
            {
                case '+':
                    added++;
                    fileAdded++;
                    newLeft--;

                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Added,
                        Text = raw[1..],
                        NewLine = newLine++,
                        HunkIndex = hunk,
                    });
                    break;

                case '-':
                    removed++;
                    fileRemoved++;
                    oldLeft--;

                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Removed,
                        Text = raw[1..],
                        OldLine = oldLine++,
                        HunkIndex = hunk,
                    });
                    break;

                default:
                    // A context line starts with a space, but git omits it for an empty
                    // line — so anything else here is context too.
                    oldLeft--;
                    newLeft--;
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        Text = raw.Length > 0 && raw[0] == ' ' ? raw[1..] : raw,
                        OldLine = oldLine++,
                        NewLine = newLine++,
                        HunkIndex = hunk,
                    });
                    break;
            }
        }

        FinishFile();

        return new DiffDocument
        {
            Lines = lines,
            Added = added,
            Removed = removed,
            IsBinary = binary,
            Hunks = hunk + 1,
            Files = files,
        };
    }

    /// <summary>
    /// Fold one header line into the title, or say it is not one to fold.
    /// </summary>
    private static bool Absorb(string raw, ref DiffFileTitle title)
    {
        if (raw.StartsWith("index ", StringComparison.Ordinal) ||
            raw.StartsWith("similarity index", StringComparison.Ordinal) ||
            raw.StartsWith("dissimilarity index", StringComparison.Ordinal))
        {
            return true;
        }

        if (raw.StartsWith("new file mode", StringComparison.Ordinal))
        {
            title = title with { Change = DiffFileChange.Added };
            return true;
        }

        if (raw.StartsWith("deleted file mode", StringComparison.Ordinal))
        {
            title = title with { Change = DiffFileChange.Deleted };
            return true;
        }

        if (raw.StartsWith("rename from ", StringComparison.Ordinal))
        {
            title = title with { Change = DiffFileChange.Renamed, OldPath = raw["rename from ".Length..] };
            return true;
        }

        if (raw.StartsWith("rename to ", StringComparison.Ordinal))
        {
            title = title with { Change = DiffFileChange.Renamed, Path = raw["rename to ".Length..] };
            return true;
        }

        if (raw.StartsWith("--- ", StringComparison.Ordinal))
        {
            // A deletion's only real path is on this side.
            if (title.Change == DiffFileChange.Deleted && raw.StartsWith("--- a/", StringComparison.Ordinal))
            {
                title = title with { Path = raw["--- a/".Length..] };
            }

            return true;
        }

        if (raw.StartsWith("+++ ", StringComparison.Ordinal))
        {
            if (raw.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                title = title with { Path = raw["+++ b/".Length..] };
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The path out of <c>diff --git a/P b/P</c>, which is ambiguous when P contains " b/".
    /// </summary>
    /// <remarks>
    /// Provisional: the <c>+++ b/</c> line, when there is one, replaces it. For the ordinary
    /// case the two halves are the same path, so the line splits exactly in the middle; that
    /// is tried first, and only then the last " b/", which is right for any rename without
    /// " b/" in its new name — and a rename's real names arrive on their own lines anyway.
    /// </remarks>
    private static string PathFromDiffGit(string raw)
    {
        var rest = raw["diff --git ".Length..];

        if (rest.Length % 2 == 1)
        {
            var half = (rest.Length - 1) / 2;
            var a = rest[..half];
            var b = rest[(half + 1)..];
            if (a.StartsWith("a/", StringComparison.Ordinal) &&
                b.StartsWith("b/", StringComparison.Ordinal) &&
                a[2..] == b[2..])
            {
                return b[2..];
            }
        }

        var split = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        return split >= 0 ? rest[(split + 3)..] : rest;
    }

    private static bool IsFileHeader(string line) =>
        line.StartsWith("diff --git", StringComparison.Ordinal) ||
        line.StartsWith("index ", StringComparison.Ordinal) ||
        line.StartsWith("--- ", StringComparison.Ordinal) ||
        line.StartsWith("+++ ", StringComparison.Ordinal) ||
        line.StartsWith("new file mode", StringComparison.Ordinal) ||
        line.StartsWith("deleted file mode", StringComparison.Ordinal) ||
        line.StartsWith("old mode", StringComparison.Ordinal) ||
        line.StartsWith("new mode", StringComparison.Ordinal) ||
        line.StartsWith("similarity index", StringComparison.Ordinal) ||
        line.StartsWith("rename from", StringComparison.Ordinal) ||
        line.StartsWith("rename to", StringComparison.Ordinal) ||
        line.StartsWith("Binary files", StringComparison.Ordinal) ||
        line.StartsWith("GIT binary patch", StringComparison.Ordinal);

    /// <summary>Read the starting line numbers out of <c>@@ -a,b +c,d @@</c>.</summary>
    /// <remarks>
    /// Only the two range tokens are read. The text after the closing <c>@@</c> is the
    /// enclosing function's first line, and it can start with <c>-</c> or <c>+</c> itself —
    /// reading every token let <c>@@ -1 +1 @@ -x</c> move the old side's start. The counts
    /// default to 1 when git leaves them out, which it does for a one-line range.
    /// </remarks>
    private static (int Old, int New, int OldCount, int NewCount) ParseHunkHeader(string header)
    {
        var old = 1;
        var @new = 1;
        var oldCount = 1;
        var newCount = 1;

        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts.Skip(1).Take(2))
        {
            if (part.Length < 2)
            {
                continue;
            }

            var numbers = part[1..].Split(',');
            if (!int.TryParse(numbers[0], out var start))
            {
                continue;
            }

            var count = numbers.Length > 1 && int.TryParse(numbers[1], out var c) ? c : 1;

            if (part[0] == '-')
            {
                old = start;
                oldCount = count;
            }
            else if (part[0] == '+')
            {
                @new = start;
                newCount = count;
            }
        }

        return (old, @new, oldCount, newCount);
    }
}
