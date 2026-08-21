using System.Globalization;
using System.Text;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Station <c>*.devices.yaml</c>: reviewable DeviceItems plus Comment SetAttribute.
/// PlugNew is never called. A missing path is a refusal, not a create.
/// </summary>
internal static partial class HostApp
{
    private static void ExportHardware(
        object project,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> warnings)
    {
        foreach (var device in Enumerate(GetProp(project, "Devices"))
                     .OrderBy(d => Str(d, "Name") ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var name = Str(device, "Name") ?? "Device";
            try
            {
                ExportOneStation(device, name, objects);
            }
            catch (Exception ex)
            {
                Warn(warnings, "EXPORT_FAILED", name, Innermost(ex));
                Emit("warning", "msg", "device catalog " + name + ": " + Innermost(ex));
            }
        }
    }

    private static void ExportOneStation(
        object device, string name, List<Dictionary<string, object?>> objects)
    {
        var dir = Path.Combine(_outDir, Sanitize(name));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Sanitize(name) + ".devices.yaml");
        var yaml = StationYaml(device, name);
        File.WriteAllText(file, yaml, Utf8NoBom);
        Record(
            objects,
            // Wire form. Must stay equal to ExportCoverage.DeviceCommentForm — this
            // project cannot reference Core (net48). TiaPortalPureTests pin the string.
            guid: "device:" + RelTo(file),
            name: name,
            type: "DEVICE",
            form: "DEVICE_COMMENT",
            path: RelTo(file),
            hash: HashTextFile(file));
        Emit("object", "path", RelTo(file), "type", "DEVICE");
    }

    private static string StationYaml(object device, string name)
    {
        var sb = new StringBuilder();
        sb.Append("schema: 1\n");
        sb.Append("vendor: tia\n");
        sb.Append("name: ").Append(YamlScalar(name)).Append('\n');
        sb.Append("path: ").Append(YamlScalar(name)).Append('\n');
        sb.Append("apply: comment\n");
        sb.Append("connectors:\n");
        foreach (var row in StationItems(device)
                     .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("  - connectorId: ").Append(YamlScalar(row.Path)).Append('\n');
            sb.Append("    interface: ").Append(YamlScalar(row.Name)).Append('\n');
            if (row.TypeIdentifier.Length > 0)
            {
                sb.Append("    moduleType: ").Append(YamlScalar(row.TypeIdentifier)).Append('\n');
            }

            sb.Append("    parameters:\n");
            foreach (var kv in row.Parameters)
            {
                sb.Append("      ").Append(YamlScalar(kv.Key)).Append(": ")
                    .Append(YamlScalar(kv.Value)).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static List<StationItem> StationItems(object root)
    {
        var rows = new List<StationItem>();
        WalkStation(root, parent: "", rows);
        return rows;
    }

    private static void WalkStation(object item, string parent, List<StationItem> into)
    {
        var path = ItemKey(parent, item);
        var name = TryStr(item, "Name") ?? item.GetType().Name;
        var typeId = TryStr(item, "TypeIdentifier") ?? TryAttribute(item, "TypeIdentifier") ?? "";
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddParam(parameters, "PositionNumber",
            TryStr(item, "PositionNumber") ?? TryAttribute(item, "PositionNumber"));
        AddParam(parameters, "Classification",
            TryStr(item, "Classification") ?? TryAttribute(item, "Classification"));
        AddParam(parameters, "Comment",
            TryAttribute(item, "Comment") ?? TryStr(item, "Comment") ?? "");
        AddParam(parameters, "TypeIdentifier", typeId);
        AddParam(parameters, "OrderNumber", TryAttribute(item, "OrderNumber"));
        AddParam(parameters, "ShortDesignation", TryAttribute(item, "ShortDesignation"));
        AddParam(parameters, "FirmwareVersion", TryAttribute(item, "FirmwareVersion"));

        into.Add(new StationItem(path, name, typeId, parameters));

        foreach (var child in Enumerate(GetProp(item, "DeviceItems"))
                     .OrderBy(c => TryStr(c, "Name") ?? "", StringComparer.OrdinalIgnoreCase)
                     .ThenBy(c => TryStr(c, "PositionNumber") ?? "", StringComparer.Ordinal))
        {
            WalkStation(child, path, into);
        }
    }

    /// <summary>
    /// Identity is parent chain + name + slot. Two <c>Port_1</c> on this fixture
    /// sit at 32769 and 33025; a name-only path would apply Comment to the last one.
    /// </summary>
    private static string ItemKey(string parent, object item)
    {
        var name = TryStr(item, "Name") ?? "Item";
        var pos = TryStr(item, "PositionNumber") ?? TryAttribute(item, "PositionNumber") ?? "";
        var piece = pos.Length == 0 ? name : name + "@" + pos;
        return parent.Length == 0 ? piece : parent + "/" + piece;
    }

    private static void AddParam(SortedDictionary<string, string> into, string key, string? value)
    {
        if (key == "Comment")
        {
            into[key] = value ?? "";
            return;
        }

        if (!string.IsNullOrEmpty(value))
        {
            into[key] = value!;
        }
    }

    private static void ApplyDeviceComments(object project, string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var wanted = CommentsFromYaml(yaml);
        if (wanted.Count == 0)
        {
            throw new InvalidOperationException(
                yamlPath + " has no Comment parameters — refusing to guess PlugNew");
        }

        var live = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Enumerate(GetProp(project, "Devices")))
        {
            IndexStation(device, parent: "", live);
        }

        foreach (var row in wanted)
        {
            if (!live.TryGetValue(row.Path, out var item))
            {
                throw new InvalidOperationException(
                    "no DeviceItem at '" + row.Path + "' — refusing PlugNew from the catalog");
            }

            var before = TryAttribute(item, "Comment") ?? TryStr(item, "Comment") ?? "";
            if (string.Equals(before, row.Comment, StringComparison.Ordinal))
            {
                continue;
            }

            if (!CanSetAttribute(item, "Comment"))
            {
                throw new InvalidOperationException(
                    "Comment on '" + row.Path + "' is not writable");
            }

            SetAttribute(item, "Comment", row.Comment);
            var after = TryAttribute(item, "Comment") ?? TryStr(item, "Comment") ?? "";
            if (!string.Equals(after, row.Comment, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Comment on '" + row.Path + "' did not take (wanted '" + row.Comment
                    + "', read '" + after + "')");
            }
        }
    }

    private static void IndexStation(object item, string parent, Dictionary<string, object> into)
    {
        var path = ItemKey(parent, item);
        into[path] = item;
        foreach (var child in Enumerate(GetProp(item, "DeviceItems")))
        {
            IndexStation(child, path, into);
        }
    }

    /// <summary>
    /// ConnectorId is the DeviceItem path; Comment is the only applied parameter.
    /// Same subset DeviceCatalog.Parse reads, so a Core test can pin the string.
    /// </summary>
    private static List<(string Path, string Comment)> CommentsFromYaml(string yaml)
    {
        var rows = new List<(string Path, string Comment)>();
        string? id = null;
        string? comment = null;
        var inParameters = false;
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith("#"))
            {
                continue;
            }

            var text = line.TrimStart(' ');
            if (text.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushComment(rows, ref id, ref comment);
                inParameters = false;
                text = text.Substring(2).TrimStart();
            }

            var colon = IndexOfUnquotedColon(text);
            if (colon < 0)
            {
                continue;
            }

            var key = UnquoteYaml(text.Substring(0, colon).Trim());
            var value = UnquoteYaml(text.Substring(colon + 1).Trim());
            if (string.Equals(key, "connectorId", StringComparison.Ordinal))
            {
                id = value;
            }
            else if (string.Equals(key, "parameters", StringComparison.Ordinal))
            {
                inParameters = true;
            }
            else if (inParameters && string.Equals(key, "Comment", StringComparison.Ordinal))
            {
                comment = value;
            }
        }

        FlushComment(rows, ref id, ref comment);
        return rows;
    }

    private static void FlushComment(
        List<(string Path, string Comment)> rows, ref string? id, ref string? comment)
    {
        if (id is not null && comment is not null)
        {
            rows.Add((id, comment));
        }

        id = null;
        comment = null;
    }

    private static int IndexOfUnquotedColon(string text)
    {
        var inQuotes = false;
        var escape = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (escape)
            {
                escape = false;
                continue;
            }

            if (inQuotes)
            {
                if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inQuotes = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
                continue;
            }

            if (ch == ':')
            {
                return i;
            }
        }

        return -1;
    }

    private static string UnquoteYaml(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[text.Length - 1] != '"')
        {
            return text;
        }

        var inner = text.Substring(1, text.Length - 2);
        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                sb.Append(inner[i]);
                continue;
            }

            i++;
            sb.Append(inner[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                var other => other,
            });
        }

        return sb.ToString();
    }

    private static string YamlScalar(string text)
    {
        if (text.Length == 0)
        {
            return "\"\"";
        }

        var plain = true;
        if (!char.IsLetter(text[0]) && text[0] != '_')
        {
            plain = false;
        }

        foreach (var ch in text)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-'))
            {
                plain = false;
                break;
            }
        }

        if (plain && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            plain = false;
        }

        if (plain)
        {
            return text;
        }

        return "\"" + text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t") + "\"";
    }

    private sealed record StationItem(
        string Path,
        string Name,
        string TypeIdentifier,
        SortedDictionary<string, string> Parameters);
}
