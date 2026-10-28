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
/// the raw diff this exists to replace. A row is modified when its type, value, access,
/// comment or attributes differ. Section headings are matched like rows and only follow their
/// section.
/// </para>
/// <para>
/// <b>Attributes are matched by name</b>, on the object and on each row alike, and come back
/// marked the way rows do: added, changed (with the old value), or removed and kept where it
/// was. An attribute is a change to a member's meaning, so a row whose only change is one is
/// a modified row.
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

            // Its lines are the older version's: the side a comment on a removed row sits on.
            var gone = row with { Change = MemberChange.Removed, Was = Describe(row), LinkGuid = null, ReferenceChanged = false, Lines = null, WasLines = row.Lines };
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
                    WasLines = old.Lines,
                    Was = Describe(old),
                    DatatypeBefore = Before(old.Datatype, row.Datatype),
                    StartValueBefore = Before(old.StartValue, row.StartValue),
                    ExternalAccessBefore = Before(old.ExternalAccess, row.ExternalAccess),
                    CommentBefore = Before(old.Comment, row.Comment),
                    Attributes = CompareAttributes(old.Attributes, row.Attributes),
                });
                modified++;
            }
            else
            {
                // Unchanged, but still on two sides: a comment on the older version's line needs it.
                rows.Add(row with { WasLines = old.Lines });
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
            Attributes = CompareAttributes(baseline.Attributes, current.Attributes),
            // By name, as attributes are: PUBLIC become PRIVATE is one chip gained and one struck,
            // which reads as the header's own words did, rather than one chip "changed from".
            Modifiers = CompareAttributes(baseline.Modifiers, current.Modifiers),
            HeaderBefore = baseline.Header,
            UnshownChange = Unshown(baseline.Unshown, current.Unshown),
            UnshownBefore = baseline.Unshown,
            // The older version's framing rides along, so coverage can vouch for a keyword line
            // that was taken away on the side it was on.
            StructuralBefore = baseline.Structural,
        };
    }

    /// <summary>
    /// The current attributes marked against the committed ones, removed ones kept after the
    /// survivor they followed, as removed rows are. Untouched when neither side has any.
    /// </summary>
    internal static IReadOnlyList<MemberAttribute> CompareAttributes(
        IReadOnlyList<MemberAttribute> baseline, IReadOnlyList<MemberAttribute> current)
    {
        if (baseline.Count == 0 && current.Count == 0)
        {
            return current;
        }

        var before = new Dictionary<string, MemberAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in baseline)
        {
            before.TryAdd(a.Name, a);
        }

        var now = current.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<MemberAttribute>(current.Count + baseline.Count);
        foreach (var a in current)
        {
            result.Add(!before.TryGetValue(a.Name, out var old) ? a with { Change = MemberChange.Added }
                : Before(old.Value, a.Value) is { } was ? a with { Change = MemberChange.Modified, ValueBefore = was, WasLines = old.Lines }
                : a with { WasLines = old.Lines });
        }

        var at = 0;
        foreach (var a in baseline)
        {
            if (now.Contains(a.Name))
            {
                at = result.FindIndex(r => string.Equals(r.Name, a.Name, StringComparison.OrdinalIgnoreCase)) + 1;
                continue;
            }

            result.Insert(at++, a with { Change = MemberChange.Removed, Lines = null, WasLines = a.Lines });
        }

        return result;
    }

    /// <summary>
    /// The sentence for a declaration that changed where the table does not draw it, or empty.
    /// </summary>
    /// <remarks>
    /// The two sides are lined up as sequences (longest common run), not compared by position:
    /// a row added above a doc comment moves the comment's line without changing it, and that
    /// is not a change the table hides. Lines are named on the newer side, or on the older one
    /// for a line that was only taken away, so a reader knows where in Raw to look.
    /// </remarks>
    private static string Unshown(IReadOnlyList<UnshownLine> baseline, IReadOnlyList<UnshownLine> current)
    {
        var n = baseline.Count;
        var m = current.Count;
        var common = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                common[i, j] = string.Equals(baseline[i].Text, current[j].Text, StringComparison.Ordinal)
                    ? common[i + 1, j + 1] + 1
                    : Math.Max(common[i + 1, j], common[i, j + 1]);
            }
        }

        var added = new List<int>();
        var gone = new List<int>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && string.Equals(baseline[x].Text, current[y].Text, StringComparison.Ordinal))
            {
                x++;
                y++;
            }
            else if (y < m && (x == n || common[x, y + 1] >= common[x + 1, y]))
            {
                added.Add(current[y++].Line);
            }
            else
            {
                gone.Add(baseline[x++].Line);
            }
        }

        // A line changed in place is one taken away and one put in; it is one change, said once.
        var changes = Math.Max(added.Count, gone.Count);
        if (changes == 0)
        {
            return string.Empty;
        }

        var where = added.Count > 0
            ? (added.Count == 1 ? "line " : "lines ") + string.Join(", ", added)
            : (gone.Count == 1 ? "line " : "lines ") + string.Join(", ", gone) + " before";
        return changes == 1
            ? $"1 declaration change is not shown in this table ({where}) - the difference is on Raw."
            : $"{changes} declaration changes are not shown in this table ({where}) - the difference is on Raw.";
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
        || !string.Equals(a.Comment, b.Comment, StringComparison.Ordinal)
        || !a.Attributes.Select(Spelled).SequenceEqual(b.Attributes.Select(Spelled), StringComparer.Ordinal);

    private static string Spelled(MemberAttribute a) => a.Name + "\u0001" + a.Value;

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

        if (row.Attributes.Count > 0)
        {
            parts.Add("{" + string.Join(", ", row.Attributes.Select(a => a.Text)) + "}");
        }

        return "At the last commit: " + string.Join(' ', parts.Where(p => p.Length > 0));
    }

    /// <summary>One sentence for the top of a marked table; shared with <see cref="CatalogDiff"/>.</summary>
    internal static string Summary(int added, int modified, int removed)
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
