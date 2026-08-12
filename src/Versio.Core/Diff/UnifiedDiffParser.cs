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

    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        _ => " ",
    };
}

/// <summary>A parsed unified diff, ready to render.</summary>
public sealed record DiffDocument
{
    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
    public int Added { get; init; }
    public int Removed { get; init; }
    public bool IsBinary { get; init; }
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

        foreach (var raw in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldLine, newLine) = ParseHunkHeader(raw);
                lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = raw });
                continue;
            }

            if (IsFileHeader(raw))
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
                lines.Add(new DiffLine { Kind = DiffLineKind.NoNewline, Text = raw });
                continue;
            }

            switch (raw[0])
            {
                case '+':
                    added++;
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Added,
                        Text = raw[1..],
                        NewLine = newLine++,
                    });
                    break;

                case '-':
                    removed++;
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Removed,
                        Text = raw[1..],
                        OldLine = oldLine++,
                    });
                    break;

                default:
                    // A context line starts with a space, but git omits it for an empty
                    // line — so anything else here is context too.
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        Text = raw.Length > 0 && raw[0] == ' ' ? raw[1..] : raw,
                        OldLine = oldLine++,
                        NewLine = newLine++,
                    });
                    break;
            }
        }

        return new DiffDocument
        {
            Lines = lines,
            Added = added,
            Removed = removed,
            IsBinary = binary,
        };
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
    private static (int Old, int New) ParseHunkHeader(string header)
    {
        var old = 1;
        var @new = 1;

        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
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

            if (part[0] == '-')
            {
                old = start;
            }
            else if (part[0] == '+')
            {
                @new = start;
            }
        }

        return (old, @new);
    }
}
