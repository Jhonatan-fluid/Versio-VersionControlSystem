using System.Globalization;
using System.Text;

namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// I/O <c>*.devices.yaml</c>: reviewable TIID tree, export-only.
/// Comment set does not persist through Save. CreateChild is never called.
/// </summary>
internal static partial class HostApp
{
    private static void ExportIo(
        object sysMan,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<Dictionary<string, string>> warnings)
    {
        var tiid = Lookup(sysMan, "TIID");
        if (tiid is null)
        {
            skipped.Add(Skip("I/O", "SERVICE_OBJECT", "TIID was not present on this project"));
            return;
        }

        try
        {
            ExportIoCatalog(tiid, objects);
        }
        catch (Exception ex)
        {
            warnings.Add(new Dictionary<string, string>
            {
                ["code"] = "EXPORT_FAILED",
                ["path"] = "I/O",
                ["detail"] = Innermost(ex),
            });
            Emit("warning", "msg", "device catalog I/O: " + Innermost(ex));
        }
    }

    private static void ExportIoCatalog(object tiid, List<Dictionary<string, object?>> objects)
    {
        // The node's Name is "Devices" on this install. A folder literally named
        // "I/O" is not a Windows path — '/' is the alt separator — so the catalog
        // lives beside the name the tree already uses, same {stem}/{stem}.devices.yaml
        // merge TIA and CODESYS already rely on.
        var name = Sanitize(TryProp(tiid, "Name") ?? "Devices");
        var dir = Path.Combine(_outDir, name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".devices.yaml");
        var yaml = IoYaml(tiid, name);
        File.WriteAllText(file, yaml, Utf8NoBom);
        var hash = HashTextFile(file);
        Record(
            objects,
            // Wire form. Must stay equal to ExportCoverage.DevicesForm — this
            // project cannot reference Core (net9 host). TwinCatPureTests pin the string.
            guid: "device:" + RelTo(file),
            name: TryProp(tiid, "Name") ?? "Devices",
            type: "DEVICE",
            form: "DEVICES",
            path: RelTo(file),
            hash: hash,
            nhash: hash);
        Emit("object", "path", RelTo(file), "type", "DEVICE");
    }

    private static string IoYaml(object tiid, string catalogPath)
    {
        var name = TryProp(tiid, "Name") ?? "Devices";
        var sb = new StringBuilder();
        sb.Append("# I/O tree, exported by Versio. Do not edit — it is rewritten on\n");
        sb.Append("# every export, and importing this file back into the project is NOT\n");
        sb.Append("# supported: Comment set in-session but does not persist through Save.\n");
        sb.Append("# Change devices in the IDE. CreateChild is refused.\n");
        sb.Append("schema: 1\n");
        sb.Append("vendor: twincat\n");
        sb.Append("name: ").Append(YamlScalar(name)).Append('\n');
        sb.Append("path: ").Append(YamlScalar(catalogPath)).Append('\n');
        sb.Append("connectors:\n");
        foreach (var row in IoCatalogRows(tiid)
                     .OrderBy(r => r.PathName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("  - connectorId: ").Append(YamlScalar(row.PathName)).Append('\n');
            sb.Append("    interface: ").Append(YamlScalar(row.Name)).Append('\n');
            if (row.ItemSubType.Length > 0)
            {
                sb.Append("    moduleType: ").Append(YamlScalar(row.ItemSubType)).Append('\n');
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

    private static List<IoCatalogRow> IoCatalogRows(object root)
    {
        var rows = new List<IoCatalogRow>();
        WalkIoItems(root, rows);
        return rows;
    }

    private static void WalkIoItems(object item, List<IoCatalogRow> into)
    {
        var name = TryProp(item, "Name") ?? "Item";
        var pathName = TryProp(item, "PathName") ?? name;
        var subType = TryProp(item, "ItemSubType") ?? "";
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        parameters["Comment"] = TryProp(item, "Comment") ?? "";
        AddIoParam(parameters, "Disabled", TryProp(item, "Disabled"));
        AddIoParam(parameters, "ItemType", TryProp(item, "ItemType"));
        AddIoParam(parameters, "ItemSubType", subType);

        into.Add(new IoCatalogRow(pathName, name, subType, parameters));

        var n = ChildCountOf(item);
        for (var i = 1; i <= n; i++)
        {
            object? child;
            try
            {
                child = ChildOf(item, i);
            }
            catch
            {
                continue;
            }

            if (child is not null)
            {
                WalkIoItems(child, into);
            }
        }
    }

    private static void AddIoParam(SortedDictionary<string, string> into, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            into[key] = value!;
        }
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

    private sealed record IoCatalogRow(
        string PathName,
        string Name,
        string ItemSubType,
        SortedDictionary<string, string> Parameters);
}
