namespace Versio.Core.Projects;

/// <summary>
/// How one catalog row - a library reference, a task, a device connector - differs from the
/// same row at the last commit.
/// </summary>
/// <remarks>
/// <para>
/// One type for every catalog rather than a set of fields per row type, so the pane draws
/// the marks the same way in each table and a catalog added later gets them by carrying one
/// property. <see cref="None"/> rather than null on an unmarked row: the pane binds through
/// it, and a null in the middle of a binding path is a quiet failure, not a blank cell.
/// </para>
/// <para>
/// <b>Never part of what a row records.</b> Each row's <c>ContentEquals</c> resets it before
/// comparing, so a marked row and its plain parse still read as the same facts - the tree
/// compares rows by that method, and a mark leaking into it would call a row changed because
/// somebody looked at it.
/// </para>
/// </remarks>
public sealed record RowMark
{
    public static readonly RowMark None = new();

    public MemberChange Change { get; init; }

    /// <summary>
    /// Every field that moved, one per line as <c>field: old → new</c>, for the row's hover:
    /// the shown cells carry the columns the table has, this carries the ones it does not
    /// (a library parameter, a watchdog time) so the table never needs a trip to Raw.
    /// </summary>
    public string Was { get; init; } = string.Empty;

    /// <summary>A modified row's committed value per column that changed; absent where it did not.</summary>
    public IReadOnlyDictionary<string, string> Before { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The committed value of <paramref name="column"/>, or null when it did not change.</summary>
    public string? BeforeOf(string column) => Before.TryGetValue(column, out var value) ? value : null;

    /// <summary>The marker column's letter: the tree's own A / M / D, blank when unchanged.</summary>
    public string Glyph => Change switch
    {
        MemberChange.Added => "A",
        MemberChange.Modified => "M",
        MemberChange.Removed => "D",
        _ => string.Empty,
    };

    /// <summary>The marker's tooltip, in words, so the letter is never the only explanation.</summary>
    public string Label => Change switch
    {
        MemberChange.Added => "Added since the last commit",
        MemberChange.Modified => "Modified since the last commit: changed cells show old → new",
        MemberChange.Removed => "Deleted since the last commit",
        _ => string.Empty,
    };

    public bool IsAdded => Change == MemberChange.Added;

    public bool IsModified => Change == MemberChange.Modified;

    public bool IsRemoved => Change == MemberChange.Removed;
}

/// <summary>
/// A catalog compared with the same file at the last commit: the rendered table, marked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The owner, 2026-10-10, on a Library Manager whose visualisation libraries had
/// all moved from 4.9 to 4.10: Raw showed six changed resolutions, the tree marked six rows,
/// and the Rendered table - the pane under a header that says DIFF - drew the current file
/// with nothing marked at all. "That's the whole point of this tool. The rendered view needs
/// to show what changed." The table now says it the way the member table has since
/// 2026-10-05: added and modified rows marked, the changed cell drawn old → new, a deleted
/// row back where it was, struck through.
/// </para>
/// <para>
/// <b>Matched by the tree's own keys</b> - a reference's name, a task's name, a connector's
/// de-duplicated key - and by each row's own <c>ContentEquals</c>, so the table and the
/// tree beside it cannot disagree about which rows changed. Never by position: inserting a
/// library would call every row under it changed.
/// </para>
/// <para>
/// <b>A null baseline is a file with no committed copy</b>, and every row is added - the same
/// call <see cref="MemberTableDiff"/> makes. The tree draws nothing in that case, on purpose
/// (twenty additions under a never-committed manager bury the panel); the table is opened
/// on purpose, by somebody asking what the file says against the last commit.
/// </para>
/// <para>Pure: two catalogs in, one out. The host reads HEAD and parses it with the same reader.</para>
/// </remarks>
public static class CatalogDiff
{
    public static LibraryCatalog Compare(LibraryCatalog? baseline, LibraryCatalog current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var (rows, summary) = Mark(
            baseline?.References.Select(r => (r.Name, r)).ToList(),
            current.References.Select(r => (r.Name, r)).ToList(),
            (a, b) => a.ContentEquals(b),
            LibraryFields,
            (r, mark) => r with { Mark = mark });

        return current with { References = rows, ChangeSummary = summary };
    }

    public static TaskCatalog Compare(TaskCatalog? baseline, TaskCatalog current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var (rows, summary) = Mark(
            baseline?.Tasks.Select(t => (t.Name, t)).ToList(),
            current.Tasks.Select(t => (t.Name, t)).ToList(),
            (a, b) => a.ContentEquals(b),
            TaskFields,
            (t, mark) => t with { Mark = mark });

        return current with { Tasks = rows, ChangeSummary = summary };
    }

    public static DeviceCatalog Compare(DeviceCatalog? baseline, DeviceCatalog current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var (rows, summary) = Mark(
            baseline?.KeyedConnectors().Select(p => (p.Key, p.Value)).ToList(),
            current.KeyedConnectors().Select(p => (p.Key, p.Value)).ToList(),
            (a, b) => a.ContentEquals(b),
            ConnectorFields,
            (c, mark) => c with { Mark = mark });

        // Empty slots have no row in the table, so a change to one would otherwise leave a
        // modified device with nothing marked. Said, not drawn: the tree has their rows.
        if (baseline is not null && !SameMap(baseline.Slots, current.Slots))
        {
            summary = summary.Length == 0
                ? "Since the last commit: its empty slots changed - the tree lists them."
                : summary + " Its empty slots changed too.";
        }

        return current with { Connectors = rows, ChangeSummary = summary };
    }

    /// <summary>The column names the pane asks <see cref="RowMark.BeforeOf"/> for.</summary>
    public static class Columns
    {
        public const string Resolution = "resolution";
        public const string Namespace = "namespace";
        public const string Kind = "kind";
        public const string Schedule = "interval";
        public const string Priority = "priority";
        public const string Watchdog = "watchdog";
        public const string Calls = "calls";
        public const string ConnectorId = "id";
        public const string Parameters = "parameters";
    }

    /// <summary>
    /// Every field a row records, shown columns first under the names the pane asks for.
    /// </summary>
    /// <remarks>
    /// The hover lists whichever of these moved, so a field missing here is a change the
    /// table marks M with nothing to say about. <c>ContentEquals</c> is what decides the row
    /// changed; this only describes it, and <see cref="Describe"/> says so when a field nobody
    /// listed was the one that moved.
    /// </remarks>
    private static IEnumerable<(string Field, string Value)> LibraryFields(LibraryReference r)
    {
        yield return (Columns.Resolution, r.DisplayVersion);
        yield return (Columns.Namespace, r.Namespace);
        yield return (Columns.Kind, r.Kind);
        yield return ("placeholder", r.Placeholder);
        yield return ("default resolution", r.DefaultResolution);
        yield return ("redirection", r.Redirection);
        yield return ("version", r.Version);
        yield return ("company", r.Company);
        yield return ("title", r.Title);
        yield return ("optional", Flag(r.Optional));
        yield return ("qualified only", Flag(r.QualifiedOnly));
        yield return ("publish symbols", Flag(r.PublishSymbols));
        foreach (var p in r.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yield return ("parameter " + p.Key, p.Value);
        }
    }

    private static IEnumerable<(string Field, string Value)> TaskFields(TaskRecord t)
    {
        yield return (Columns.Schedule, t.DisplaySchedule);
        yield return (Columns.Priority, t.Priority);
        yield return (Columns.Watchdog, t.WatchdogDisplay);
        yield return (Columns.Calls, t.CallsSummary);
        yield return ("core binding", t.CoreBinding);
        yield return ("watchdog time", t.WatchdogTime);
        yield return ("watchdog sensitivity", t.WatchdogSensitivity);
    }

    private static IEnumerable<(string Field, string Value)> ConnectorFields(DeviceConnector c)
    {
        yield return (Columns.ConnectorId, c.ConnectorId);
        yield return (Columns.Parameters, c.ParameterCount);
        yield return ("interface", c.Interface);
        yield return ("module type", c.ModuleType);
        foreach (var p in c.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yield return ("parameter " + p.Key, p.Value);
        }
    }

    private static string Flag(bool value) => value ? "yes" : "no";

    /// <summary>
    /// The shared walk: current rows in their own order, each marked, with every row the
    /// baseline had and the current file does not re-inserted after the nearest row before it
    /// that survived - where a reader last saw it.
    /// </summary>
    private static (List<T> Rows, string Summary) Mark<T>(
        IReadOnlyList<(string Key, T Row)>? baseline,
        IReadOnlyList<(string Key, T Row)> current,
        Func<T, T, bool> same,
        Func<T, IEnumerable<(string Field, string Value)>> fields,
        Func<T, RowMark, T> withMark)
    {
        // A key repeated within one side is paired by occurrence, the second with the second.
        // Keyed by the key alone, every repeat after the first was compared with the FIRST
        // baseline row, so an unchanged file whose four rows shared one key (a reader that had
        // left their names empty, FINDINGS 2026-10-23) reported three of them as changed.
        // The first occurrence keeps its own key, so nothing about an ordinary catalog changes.
        baseline = baseline is null ? null : ByOccurrence(baseline);
        current = ByOccurrence(current);

        var before = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (key, row) in baseline ?? [])
        {
            before.TryAdd(key, row);
        }

        var nowKeys = current.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var removedAfter = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        var removedAtStart = new List<T>();
        string? lastSurvivor = null;
        var removed = 0;

        foreach (var (key, row) in baseline ?? [])
        {
            if (nowKeys.Contains(key))
            {
                lastSurvivor = key;
                continue;
            }

            var gone = withMark(row, new RowMark { Change = MemberChange.Removed, Was = "Deleted since the last commit." });
            removed++;
            if (lastSurvivor is null)
            {
                removedAtStart.Add(gone);
            }
            else
            {
                (removedAfter.TryGetValue(lastSurvivor, out var list) ? list : removedAfter[lastSurvivor] = []).Add(gone);
            }
        }

        var rows = new List<T>(current.Count + removed);
        rows.AddRange(removedAtStart);
        int added = 0, modified = 0;

        foreach (var (key, row) in current)
        {
            if (!before.TryGetValue(key, out var old))
            {
                rows.Add(withMark(row, new RowMark { Change = MemberChange.Added, Was = "Added since the last commit." }));
                added++;
            }
            else if (!same(old, row))
            {
                rows.Add(withMark(row, Describe(fields(old).ToList(), fields(row).ToList())));
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

        return (rows, MemberTableDiff.Summary(added, modified, removed));
    }

    /// <summary>Each key made unique by its occurrence: the first as it is, later ones suffixed.</summary>
    /// <remarks>The suffix starts with a control character no catalog key contains.</remarks>
    private static List<(string Key, T Row)> ByOccurrence<T>(IReadOnlyList<(string Key, T Row)> rows)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<(string Key, T Row)>(rows.Count);
        foreach (var (key, row) in rows)
        {
            var n = seen.TryGetValue(key, out var count) ? count : 0;
            seen[key] = n + 1;
            result.Add((n == 0 ? key : key + "\u0001" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), row));
        }

        return result;
    }

    /// <summary>A modified row's mark: the committed value of each field that moved.</summary>
    /// <remarks>
    /// Fields are matched by name, not position: a library parameter that appeared or went
    /// away shifts every field listed after it, and a positional match would report all of
    /// them. An empty old value comes back as "(empty)", as on the member table - a value that
    /// appeared where there was none is a change, and a struck-out blank reads as nothing.
    /// </remarks>
    private static RowMark Describe(
        IReadOnlyList<(string Field, string Value)> old,
        IReadOnlyList<(string Field, string Value)> now)
    {
        var oldByField = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, value) in old)
        {
            oldByField.TryAdd(field, value);
        }

        var nowFields = now.Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
        var beforeCells = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var (field, value) in now)
        {
            var was = oldByField.TryGetValue(field, out var o) ? o : string.Empty;
            if (string.Equals(was, value, StringComparison.Ordinal))
            {
                continue;
            }

            beforeCells.TryAdd(field, was.Length == 0 ? "(empty)" : was);
            lines.Add($"{field}: {Shown(was)} → {Shown(value)}");
        }

        foreach (var (field, value) in old)
        {
            if (!nowFields.Contains(field))
            {
                lines.Add($"{field}: {Shown(value)} → (gone)");
            }
        }

        if (lines.Count == 0)
        {
            // ContentEquals said changed and no listed field moved: a field was added to the
            // row type without being described above. Say so rather than show an M over nothing.
            lines.Add("changed in a field this table does not describe - the difference is on Raw");
        }

        return new RowMark
        {
            Change = MemberChange.Modified,
            Was = "Since the last commit:\n" + string.Join('\n', lines),
            Before = beforeCells,
        };
    }

    private static string Shown(string value) => value.Length == 0 ? "(empty)" : value;

    private static bool SameMap(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count
        && a.All(p => b.TryGetValue(p.Key, out var v) && string.Equals(p.Value, v, StringComparison.Ordinal));
}
