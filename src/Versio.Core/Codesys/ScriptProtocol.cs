using System.Text.Json;

namespace Versio.Core.Codesys;

/// <summary>One JSON-lines event emitted by a Versio python script.</summary>
public sealed record ScriptEvent
{
    public required string Type { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Fields { get; init; }
        = new Dictionary<string, JsonElement>();

    /// <summary>The raw JSON, kept for the log and for diagnostics.</summary>
    public string Raw { get; init; } = string.Empty;

    public string? String(string name) =>
        Fields.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public int? Int(string name) =>
        Fields.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var i)
            ? i
            : null;

    public bool? Bool(string name) =>
        Fields.TryGetValue(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}

/// <summary>Everything parsed out of one script run.</summary>
public sealed record ScriptOutput
{
    public IReadOnlyList<ScriptEvent> Events { get; init; } = [];

    /// <summary>Non-protocol output: CODESYS's own messages, stack traces, licence notices.
    /// Kept because when something goes wrong this is usually where the reason is.</summary>
    public IReadOnlyList<string> Chatter { get; init; } = [];

    /// <summary>Lines that carried the marker but whose JSON would not parse.</summary>
    public IReadOnlyList<string> Malformed { get; init; } = [];

    public ScriptEvent? Summary => Events.LastOrDefault(e => e.Type == "summary");
    public ScriptEvent? Fatal => Events.FirstOrDefault(e => e.Type == "fatal");
    public IEnumerable<ScriptEvent> Warnings => Events.Where(e => e.Type == "warning");
    public bool Completed => Events.Any(e => e.Type == "done");

    /// <summary>Script-reported exit code, distinct from the process exit code.</summary>
    public int? ScriptExitCode => Events.LastOrDefault(e => e.Type == "exit")?.Int("rc");
}

/// <summary>
/// Parses the JSON-lines protocol the python scripts emit.
/// </summary>
/// <remarks>
/// <para>
/// Scripts prefix every structured line with <see cref="Marker"/> so protocol output can be
/// separated from CODESYS's own chatter, which shares the same stdout and cannot be
/// silenced. Scraping prose was never an option: the messages are localised and change
/// between service packs.
/// </para>
/// <para>
/// Parsing is deliberately total. A malformed line is collected, never thrown on — a
/// script that dies mid-write must still yield everything it managed to emit, because that
/// is exactly when the diagnostics matter most.
/// </para>
/// </remarks>
public static class ScriptProtocol
{
    public const string Marker = "@@VERSIO@@";

    public static ScriptOutput Parse(string? stdout)
    {
        var events = new List<ScriptEvent>();
        var chatter = new List<string>();
        var malformed = new List<string>();

        if (string.IsNullOrEmpty(stdout))
        {
            return new ScriptOutput();
        }

        foreach (var rawLine in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            var idx = rawLine.IndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                if (!string.IsNullOrWhiteSpace(rawLine))
                {
                    chatter.Add(rawLine.TrimEnd());
                }

                continue;
            }

            // CODESYS sometimes writes its own text ahead of ours on the same line.
            var before = rawLine[..idx];
            if (!string.IsNullOrWhiteSpace(before))
            {
                chatter.Add(before.TrimEnd());
            }

            var json = rawLine[(idx + Marker.Length)..];
            var parsed = TryParse(json);
            if (parsed is null)
            {
                malformed.Add(json);
            }
            else
            {
                events.Add(parsed);
            }
        }

        return new ScriptOutput
        {
            Events = events,
            Chatter = chatter,
            Malformed = malformed,
        };
    }

    private static ScriptEvent? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("t", out var t) ||
                t.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name != "t")
                {
                    fields[prop.Name] = prop.Value.Clone();
                }
            }

            return new ScriptEvent
            {
                Type = t.GetString()!,
                Fields = fields,
                Raw = json,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
