namespace Versio.Core.Projects;

/// <summary>
/// One task, as the project's task configuration declares it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vendor-neutral on purpose</b>, like <see cref="LibraryReference"/>: every PLC toolchain
/// schedules programs somehow, and the questions a reviewer asks — what runs, how often, at
/// what priority, watched by what — are the same across them. What is vendor-specific is the
/// code that produces the file: <c>versio_tasks.py</c> for CODESYS.
/// </para>
/// <para>
/// <b>Every scheduling value is a verbatim string.</b> CODESYS stores <c>t#20ms</c> on one
/// task and <c>100</c> on another in the same project; normalising either side would put a
/// diff in front of somebody that they did not make, and this file is the importable half —
/// what is read here is what a typed import applies. The one exception the emitter makes is
/// <c>coreBinding</c>, an int in the file; it is still held as text here because this type
/// exists to display and compare, not to compute with a core number.
/// </para>
/// </remarks>
public sealed record TaskRecord
{
    /// <summary>The task's name. Never empty in a valid file.</summary>
    public required string Name { get; init; }

    /// <summary>Scheduling priority, verbatim — <c>"0"</c> is a real value, absent is not it.</summary>
    public string Priority { get; init; } = string.Empty;

    /// <summary>Cycle interval, verbatim — <c>t#20ms</c> and <c>100</c> both occur.</summary>
    public string Interval { get; init; } = string.Empty;

    /// <summary>The unit the interval is in, when the interval alone does not say.</summary>
    public string IntervalUnit { get; init; } = string.Empty;

    /// <summary>Which CPU core the task is bound to. <c>-1</c> is unbound; empty is unrecorded.</summary>
    public string CoreBinding { get; init; } = string.Empty;

    public bool WatchdogEnabled { get; init; }
    public string WatchdogTime { get; init; } = string.Empty;
    public string WatchdogSensitivity { get; init; } = string.Empty;

    /// <summary>The POUs this task calls, in the task's own order.</summary>
    public IReadOnlyList<string> Pous { get; init; } = [];

    /// <summary>The schedule in one phrase, for the row beside the task's name.</summary>
    /// <remarks>
    /// <c>t#20ms</c> already carries its unit; a bare number does not, so the unit is
    /// appended only when it adds something. Verbatim otherwise — the format is the
    /// project's, not ours.
    /// </remarks>
    public string DisplaySchedule =>
        Interval.Length == 0 ? string.Empty
        : IntervalUnit.Length > 0 && Interval.All(char.IsAsciiDigit)
            ? $"{Interval} {IntervalUnit}"
            : Interval;

    /// <summary>The POUs this task calls, as one cell of a table.</summary>
    public string CallsSummary => Pous.Count == 0 ? "—" : string.Join(", ", Pous);

    /// <summary>Whether two records declare the same task.</summary>
    /// <remarks>
    /// Same construction as <see cref="LibraryReference.ContentEquals"/>, for the same two
    /// reasons: record equality compares <see cref="Pous"/> by reference, so two parses of
    /// one file would read as different — and a comparison written as record equality over
    /// clones covers any field added to this type later without anyone extending a list. The
    /// POU list is compared in order, because the order is the task's own and a reorder is a
    /// real change.
    /// </remarks>
    public bool ContentEquals(TaskRecord other)
    {
        if (this with { Pous = NoPous } != other with { Pous = NoPous })
        {
            return false;
        }

        return Pous.SequenceEqual(other.Pous, StringComparer.Ordinal);
    }

    private static readonly IReadOnlyList<string> NoPous = [];
}

/// <summary>
/// The contents of one <c>*.tasks.yaml</c> file.
/// </summary>
/// <remarks>
/// <para>
/// Written by the export and — unlike <see cref="LibraryCatalog"/> — <b>applied back</b>: this
/// is the importable half of a task configuration, applied through typed property assignment
/// so that <c>import_native</c> never sees the object (G7/G11). The limits are deliberate and
/// live with the importer, not here: a task named in the YAML but absent from the project is
/// warned about and never created, and a task deleted from the YAML is left alone.
/// </para>
/// <para>
/// This reader existed as a plan item — "no reader without a consumer" — until the tree grew
/// the consumer: one row per task under its configuration, marked against HEAD's copy the
/// same way library references are.
/// </para>
/// </remarks>
public sealed record TaskCatalog
{
    /// <summary>
    /// What every task catalog's file name ends with, whichever vendor exported it.
    /// </summary>
    /// <remarks>
    /// Neutral for the same reason <see cref="LibraryCatalog.FileSuffix"/> is, and used the
    /// same way: the shell recognises a changed catalog by suffix before any manifest is
    /// parsed. The writing side is <c>versio_types.py</c>; the committed fixture's file and
    /// the shell flow test over it are what pin the two languages together.
    /// </remarks>
    public const string FileSuffix = ".tasks.yaml";

    /// <summary>Document format version. Zero means the file could not be read.</summary>
    public int Schema { get; init; }

    /// <summary>Which toolchain produced this file, lower-case.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>The task configuration object's own name.</summary>
    public string Configuration { get; init; } = string.Empty;

    /// <summary>Path of the configuration within the export tree, forward slashes.</summary>
    public string Path { get; init; } = string.Empty;

    public IReadOnlyList<TaskRecord> Tasks { get; init; } = [];

    public static readonly TaskCatalog Empty = new();

    /// <summary>True when the file parsed and described a real configuration.</summary>
    public bool IsValid => Schema > 0;

    /// <summary>
    /// Parse one <c>*.tasks.yaml</c> document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hand-written, and reads only the subset <c>versio_yaml.py</c> emits</b> — a header
    /// mapping, then a list of mappings whose one nested shape is a scalar list
    /// (<c>pous</c>). That nested list is why this walk is not
    /// <see cref="LibraryCatalog.Parse"/> with the names changed: there, a dash line is
    /// always a new reference; here, a dash line deeper than the task's own keys is an entry
    /// in the open list, and only a dash at or above the item's indent starts a new task.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> Read to draw a tree; the file can be mid-write; same rule as
    /// every parser beside it.
    /// </para>
    /// </remarks>
    public static TaskCatalog Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Empty;
        }

        var top = new Dictionary<string, string>(StringComparer.Ordinal);
        var tasks = new List<TaskRecord>();

        Dictionary<string, string>? item = null;
        Dictionary<string, List<string>>? lists = null;
        List<string>? openList = null;
        var itemIndent = 0;
        var inTasks = false;

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            var text = line.TrimStart(' ');

            if (text.StartsWith("- ", StringComparison.Ordinal))
            {
                // Deeper than this task's own keys: an entry of the scalar list opened
                // above, not a new task. Checked first, or every POU would flush the task
                // that owns it and the file would read as one task per POU.
                if (item is not null && indent > itemIndent)
                {
                    openList?.Add(CatalogYaml.Unquote(text[2..].Trim()));
                    continue;
                }

                Flush(item, lists, tasks);
                item = new Dictionary<string, string>(StringComparer.Ordinal);
                lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                openList = null;
                text = text[2..].TrimStart();
                indent += 2;
                itemIndent = indent;
            }

            if (!CatalogYaml.Split(text, out var key, out var value))
            {
                continue;
            }

            if (indent == 0)
            {
                Flush(item, lists, tasks);
                item = null;
                lists = null;
                openList = null;

                if (key == "tasks")
                {
                    inTasks = true;
                    continue;
                }

                inTasks = false;
                top[key] = value;
                continue;
            }

            if (!inTasks || item is null || lists is null)
            {
                continue;
            }

            if (value.Length == 0)
            {
                // A key with nothing after the colon opens a scalar list — or records a
                // field whose value is absent, and the next line says which. An empty list
                // and an absent value mean the same thing here.
                openList = [];
                lists[key] = openList;
                continue;
            }

            item[key] = value;
        }

        Flush(item, lists, tasks);

        var schema = int.TryParse(CatalogYaml.Value(top, "schema"), out var s) ? s : 0;
        return new TaskCatalog
        {
            Schema = schema,
            Vendor = CatalogYaml.Value(top, "vendor"),
            Configuration = CatalogYaml.Value(top, "configuration"),
            Path = CatalogYaml.Value(top, "path"),
            Tasks = tasks,
        };
    }

    /// <summary>Turn the key/value pairs collected for one item into a task.</summary>
    private static void Flush(
        Dictionary<string, string>? item,
        Dictionary<string, List<string>>? lists,
        List<TaskRecord> into)
    {
        if (item is null)
        {
            return;
        }

        var name = CatalogYaml.Value(item, "name");
        if (string.IsNullOrEmpty(name))
        {
            // A task with no name is not a task. The file is machine-written, so this only
            // happens when it was truncated mid-write, and half an entry is not information.
            return;
        }

        into.Add(new TaskRecord
        {
            Name = name,
            Priority = CatalogYaml.Value(item, "priority"),
            Interval = CatalogYaml.Value(item, "interval"),
            IntervalUnit = CatalogYaml.Value(item, "intervalUnit"),
            CoreBinding = CatalogYaml.Value(item, "coreBinding"),
            WatchdogEnabled = CatalogYaml.Flag(item, "watchdogEnabled"),
            WatchdogTime = CatalogYaml.Value(item, "watchdogTime"),
            WatchdogSensitivity = CatalogYaml.Value(item, "watchdogSensitivity"),
            Pous = lists is not null && lists.TryGetValue("pous", out var pous)
                ? pous
                : [],
        });
    }
}
