namespace Versio.Core.Projects;

/// <summary>
/// A member table compared with the same object's table at the last commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the table, not only the diff.</b> The owner followed a link from a block to a changed
/// UDT on 2026-10-05 and landed on its raw diff: twenty-three lines of SCL attribute blocks
/// to find one changed member in. "That's the whole point, it needs to show on the table."
/// So the table carries the change: added and modified rows are marked, a removed row comes
/// back where it was, struck through, and a modified row keeps what it said before.
/// </para>
/// <para>
/// <b>Matched by section and path, never by position.</b> Inserting a member shifts every row
/// under it; a positional compare would call all of them changed, which is the same noise as
/// the raw diff this exists to replace. A row is modified when its type, value, access or
/// comment differs. Section headings are matched like rows and only follow their section.
/// </para>
/// <para>
/// Pure: two tables in, one out. The host reads the committed file and parses it with the
/// same format that parsed the working one, so both sides were read by the same rules.
/// </para>
/// </remarks>
public static class MemberTableDiff
{
    public static MemberTable Compare(MemberTable baseline, MemberTable current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var before = new Dictionary<string, MemberRow>(StringComparer.Ordinal);
        foreach (var row in baseline.Members)
        {
            before.TryAdd(Key(row), row);
        }

        var nowKeys = current.Members.Select(Key).ToHashSet(StringComparer.Ordinal);

        // Each removed row is re-inserted after the nearest row before it (in the committed
        // order) that still exists, so it reappears where a reader last saw it.
        var removedAfter = new Dictionary<string, List<MemberRow>>(StringComparer.Ordinal);
        var removedAtStart = new List<MemberRow>();
        string? lastSurvivor = null;
        foreach (var row in baseline.Members)
        {
            var key = Key(row);
            if (nowKeys.Contains(key))
            {
                lastSurvivor = key;
                continue;
            }

            var gone = row with { Change = MemberChange.Removed, Was = Describe(row), LinkGuid = null, ReferenceChanged = false };
            if (lastSurvivor is null)
            {
                removedAtStart.Add(gone);
            }
            else
            {
                (removedAfter.TryGetValue(lastSurvivor, out var list) ? list : removedAfter[lastSurvivor] = []).Add(gone);
            }
        }

        var rows = new List<MemberRow>(current.Members.Count + removedAtStart.Count);
        rows.AddRange(removedAtStart);
        int added = 0, modified = 0;

        foreach (var row in current.Members)
        {
            var key = Key(row);
            if (!before.TryGetValue(key, out var old))
            {
                rows.Add(row.IsSectionHeading ? row : row with { Change = MemberChange.Added });
                added += row.IsSectionHeading ? 0 : 1;
            }
            else if (!row.IsSectionHeading && Differs(old, row))
            {
                rows.Add(row with
                {
                    Change = MemberChange.Modified,
                    Was = Describe(old),
                    DatatypeBefore = Before(old.Datatype, row.Datatype),
                    StartValueBefore = Before(old.StartValue, row.StartValue),
                    ExternalAccessBefore = Before(old.ExternalAccess, row.ExternalAccess),
                    CommentBefore = Before(old.Comment, row.Comment),
                });
                modified++;
            }
            else
            {
                rows.Add(row);
            }

            if (removedAfter.TryGetValue(key, out var gone))
            {
                rows.AddRange(gone);
            }
        }

        var removed = rows.Count(r => r.IsRemoved && !r.IsSectionHeading);

        return current with
        {
            Members = rows,
            ChangeSummary = Summary(added, modified, removed),
        };
    }

    /// <summary>
    /// The old value when the field changed, else null. An empty old value comes back as
    /// "(empty)": a value that appeared where there was none is still a change worth seeing,
    /// and an empty struck-out cell would read as nothing.
    /// </summary>
    private static string? Before(string old, string now) =>
        string.Equals(old, now, StringComparison.Ordinal) ? null : old.Length == 0 ? "(empty)" : old;

    private static string Key(MemberRow row) =>
        (row.IsSectionHeading ? "§" : row.Section + "\u0001") + row.Path;

    private static bool Differs(MemberRow a, MemberRow b) =>
        !string.Equals(a.Datatype, b.Datatype, StringComparison.Ordinal)
        || !string.Equals(a.StartValue, b.StartValue, StringComparison.Ordinal)
        || !string.Equals(a.ExternalAccess, b.ExternalAccess, StringComparison.Ordinal)
        || !string.Equals(a.Comment, b.Comment, StringComparison.Ordinal);

    /// <summary>The committed row as one line: type, value, access and comment, the ones it had.</summary>
    private static string Describe(MemberRow row)
    {
        var parts = new List<string> { row.Datatype };
        if (row.StartValue.Length > 0)
        {
            parts.Add(":= " + row.StartValue);
        }

        if (row.ExternalAccess.Length > 0)
        {
            parts.Add("· " + row.ExternalAccess);
        }

        if (row.Comment.Length > 0)
        {
            parts.Add("// " + row.Comment);
        }

        return "At the last commit: " + string.Join(' ', parts.Where(p => p.Length > 0));
    }

    private static string Summary(int added, int modified, int removed)
    {
        var parts = new List<string>();
        if (modified > 0)
        {
            parts.Add($"{modified} changed");
        }

        if (added > 0)
        {
            parts.Add($"{added} added");
        }

        if (removed > 0)
        {
            parts.Add($"{removed} removed");
        }

        return parts.Count == 0
            ? string.Empty
            : $"Since the last commit: {string.Join(", ", parts)} - marked below.";
    }
}
