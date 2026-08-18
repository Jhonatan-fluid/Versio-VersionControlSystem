namespace Versio.Core.Diff;

/// <summary>One row of the split view: the old file on the left, the new on the right.</summary>
/// <remarks>
/// Exactly one of three shapes: a spanning line (headers, hunk markers), a paired row
/// (context on both sides, or an edit), or a one-sided row (a pure insertion or deletion,
/// blank on the side that has no such line). The view renders what is here and decides
/// nothing — the same split the unified view keeps with its parser.
/// </remarks>
public sealed record SideBySideRow
{
    /// <summary>The old side's line — context or removed — or null for a blank cell.</summary>
    public DiffLine? Left { get; init; }

    /// <summary>The new side's line — context or added — or null for a blank cell.</summary>
    public DiffLine? Right { get; init; }

    /// <summary>A line that spans both columns: file headers, hunk headers, no-newline.</summary>
    public DiffLine? Spanning { get; init; }
}

/// <summary>
/// Folds a unified diff into side-by-side rows.
/// </summary>
/// <remarks>
/// <para>
/// Built from the parsed unified document rather than from a second diff, for the parser's
/// own reason: a separate diff implementation could disagree with the one git printed, and
/// then the two view modes would show different changes for one file.
/// </para>
/// <para>
/// Pairing follows the format's shape, as <see cref="IntralineEmphasis"/> does: one change
/// run is every removed line then every added line, and the i-th of each block share a row.
/// The longer block's tail gets one-sided rows — that is what a pure insertion or deletion
/// is, and padding it with fake pairings would draw edits that never happened.
/// </para>
/// </remarks>
public static class SideBySideDiff
{
    public static IReadOnlyList<SideBySideRow> Build(DiffDocument document)
    {
        var rows = new List<SideBySideRow>(document.Lines.Count);
        var lines = document.Lines;

        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i];

            if (line.Kind is DiffLineKind.HunkHeader
                          or DiffLineKind.FileHeader
                          or DiffLineKind.NoNewline)
            {
                rows.Add(new SideBySideRow { Spanning = line });
                i++;
                continue;
            }

            if (line.Kind == DiffLineKind.Context)
            {
                rows.Add(new SideBySideRow { Left = line, Right = line });
                i++;
                continue;
            }

            // A change run: removed block, then added block, paired index-wise.
            var removedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed)
            {
                i++;
            }

            var addedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added)
            {
                i++;
            }

            var removedCount = addedStart - removedStart;
            var addedCount = i - addedStart;

            for (var p = 0; p < Math.Max(removedCount, addedCount); p++)
            {
                rows.Add(new SideBySideRow
                {
                    Left = p < removedCount ? lines[removedStart + p] : null,
                    Right = p < addedCount ? lines[addedStart + p] : null,
                });
            }

            // An added block with no removed block ahead of it lands here with both counts
            // honest: removedCount is zero and the loop above emitted right-only rows.
            if (removedCount == 0 && addedCount == 0)
            {
                // A kind this method does not know. Skip one line rather than loop forever;
                // the unified view still shows it.
                i++;
            }
        }

        return rows;
    }
}
