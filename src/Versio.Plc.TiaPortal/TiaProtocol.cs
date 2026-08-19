using System.Text.Json;

namespace Versio.Plc.TiaPortal;

/// <summary>One JSON-lines event emitted by the TIA Portal host.</summary>
public sealed record TiaEvent
{
    public required string Type { get; init; }

    public IReadOnlyDictionary<string, JsonElement> Fields { get; init; }
        = new Dictionary<string, JsonElement>();

    public string Raw { get; init; } = string.Empty;

    public string? String(string name) =>
        Fields.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>Everything parsed out of one host run.</summary>
public sealed record TiaOutput
{
    public IReadOnlyList<TiaEvent> Events { get; init; } = [];

    public IReadOnlyList<string> Chatter { get; init; } = [];

    public IReadOnlyList<string> Malformed { get; init; } = [];

    public TiaEvent? Fatal => Events.FirstOrDefault(e => e.Type == "fatal");

    public bool Completed => Events.Any(e => e.Type == "done");
}

/// <summary>
/// Parses the JSON-lines protocol the host emits.
/// </summary>
/// <remarks>
/// Same marker as the first vendor's scripts, on purpose: one filter, one heartbeat, one
/// way to tell protocol from the toolchain's own chatter. The field name is <c>t</c>,
/// matching that protocol, so a second emitter is not a second language.
/// </remarks>
public static class TiaProtocol
{
    public const string Marker = "@@VERSIO@@";

    public static TiaOutput Parse(string? stdout)
    {
        var events = new List<TiaEvent>();
        var chatter = new List<string>();
        var malformed = new List<string>();

        if (string.IsNullOrEmpty(stdout))
        {
            return new TiaOutput();
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

        return new TiaOutput
        {
            Events = events,
            Chatter = chatter,
            Malformed = malformed,
        };
    }

    private static TiaEvent? TryParse(string json)
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

            return new TiaEvent
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
