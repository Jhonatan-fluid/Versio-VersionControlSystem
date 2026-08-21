using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Versio.Plc.TwinCat.Host;

internal static partial class HostApp
{
    private static string _outDir = "";

    // Beckhoff TREEITEMSUBTYP_* — visualisation is not PLC software for this loop.
    private const int SubTypeVisualisation = 619;
    private const int SubTypeFolder = 601;

    private static void Export(object sysMan, object plcRoot, string outDir)
    {
        _outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var objects = new List<Dictionary<string, object?>>();
        var skipped = new List<Dictionary<string, string>>();
        var warnings = new List<Dictionary<string, string>>();

        var plcName = Sanitize(Com.Str(plcRoot, "Name") ?? "PLC");
        var root = Path.Combine(outDir, plcName);
        Directory.CreateDirectory(root);

        Emit("progress", "phase", "export", "detail", plcName);
        WalkTree(plcRoot, root, objects, skipped, warnings);

        skipped.Add(Skip("NC", "SERVICE_OBJECT", "TINC motion is a coverage skip"));
        skipped.Add(Skip("Safety", "SERVICE_OBJECT", "safety projects are a coverage skip"));
        skipped.Add(Skip("C++", "SERVICE_OBJECT", "C++ projects are a coverage skip"));
        skipped.Add(Skip("Visualisation", "SERVICE_OBJECT", "visualisations are a coverage skip"));

        if (Lookup(sysMan, "TINC") is not null)
        {
            Emit("info", "msg", "TINC present — coverage skip");
        }

        // A mirror with zero PLC objects is worse than no mirror: it looks like a
        // successful export in which every object was deleted. Count PLC before the
        // I/O catalog is recorded — a yaml-only mirror would hide a failed TIPC walk.
        if (objects.Count == 0)
        {
            throw new InvalidOperationException(
                "the export found no PLC software under " + plcName + " — refusing to " +
                "write an empty mirror, which would read as every object deleted");
        }

        // Comment set in-session (ioprobe 2026-08-20) and ProduceXml showed the
        // marker; File.SaveAll did not persist it and SaveProject is
        // DISP_E_UNKNOWNNAME. Form DEVICES, export-only. YAML write must not
        // gate the PLC walk: a failed TIID dump still leaves .st in the mirror.
        ExportIo(sysMan, objects, skipped, warnings);

        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 10,
            ["vendor"] = "TwinCat",
            ["objects"] = objects,
            ["skipped"] = skipped,
            ["warnings"] = warnings,
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outDir, "_manifest.json"), json, Utf8NoBom);

        var md = new StringBuilder();
        md.AppendLine("# Export coverage");
        md.AppendLine();
        md.AppendLine($"Objects exported: {objects.Count}");
        md.AppendLine($"Skipped: {skipped.Count}");
        md.AppendLine();
        md.AppendLine("I/O is exported as *.devices.yaml (export-only; Comment does not persist through Save; never CreateChild).");
        md.AppendLine("NC, safety, C++ and visualisations are coverage skips.");
        md.AppendLine("Graphical POUs export for review only when DeclarationText is unavailable.");
        md.AppendLine("LibPics images are documentation assets, not PLC software.");
        File.WriteAllText(Path.Combine(outDir, "_coverage.md"), md.ToString(), Utf8NoBom);
    }

    private static void WalkTree(
        object item, string dir,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<Dictionary<string, string>> warnings)
    {
        var childCount = Com.Int(item, "ChildCount");
        for (var i = 1; i <= childCount; i++)
        {
            object? child;
            try
            {
                child = ChildOf(item, i);
            }
            catch (Exception ex)
            {
                warnings.Add(new Dictionary<string, string>
                {
                    ["code"] = "CHILD",
                    ["path"] = dir,
                    ["detail"] = Innermost(ex),
                });
                continue;
            }

            if (child is null)
            {
                continue;
            }

            ExportItem(child, dir, objects, skipped, warnings);
        }
    }

    private static void ExportItem(
        object item, string dir,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<Dictionary<string, string>> warnings)
    {
        var name = Com.Str(item, "Name") ?? "Item";
        var subType = Com.Int(item, "ItemSubType");
        var pathName = Com.Str(item, "PathName") ?? name;

        if (subType == SubTypeVisualisation)
        {
            skipped.Add(Skip(pathName, "SERVICE_OBJECT", "visualisations are a coverage skip"));
            return;
        }

        var decl = TryText(item, "DeclarationText");
        var impl = TryText(item, "ImplementationText");
        var childCount = Com.Int(item, "ChildCount");

        if (decl is null && impl is null)
        {
            if (childCount > 0 || subType == SubTypeFolder)
            {
                var nested = Path.Combine(dir, Sanitize(name));
                Directory.CreateDirectory(nested);
                WalkTree(item, nested, objects, skipped, warnings);
                return;
            }

            skipped.Add(Skip(pathName, "SERVICE_OBJECT",
                "no DeclarationText / ImplementationText — not textual PLC software"));
            return;
        }

        Directory.CreateDirectory(dir);
        var relFile = Path.Combine(dir, Sanitize(name) + ".st");
        var packed = HostText.Join(decl, impl);
        File.WriteAllText(relFile, packed, Utf8NoBom);

        var form = string.IsNullOrEmpty(impl) ? "ST_DECL" : "ST_FULL";
        var type = TypeOf(decl, subType);
        var hash = HashTextFile(relFile);
        Record(objects, pathName, name, type, form, RelTo(relFile), hash, hash);
        Emit("object", "path", RelTo(relFile), "type", type);

        if (childCount > 0)
        {
            WalkTree(item, Path.Combine(dir, Sanitize(name)), objects, skipped, warnings);
        }
    }

    private static string? TryText(object item, string property)
    {
        try
        {
            var value = Com.Str(item, property);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        catch
        {
            // ITcSmTreeItem does not always surface ITcPlcDeclaration through IDispatch.
        }

        if (property is "DeclarationText" or "ImplementationText")
        {
            return TextFromXml(item, property == "DeclarationText");
        }

        return null;
    }

    /// <summary>
    /// DeclarationText is a separate COM interface. When IDispatch does not see it,
    /// ProduceXml is still the Automation Interface — not a disk parse of <c>.TcPOU</c>.
    /// </summary>
    private static string? TextFromXml(object item, bool declaration)
    {
        string xml;
        try
        {
            xml = Com.Call(item, "ProduceXml")?.ToString() ?? "";
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            if (declaration)
            {
                var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Declaration");
                return el?.Value;
            }

            var st = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "ST" && e.Parent?.Name.LocalName == "Implementation");
            return st?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string TypeOf(string? declaration, int subType)
    {
        var text = declaration ?? "";
        if (ContainsKeyword(text, "FUNCTION_BLOCK"))
        {
            return "POU";
        }

        if (ContainsKeyword(text, "PROGRAM"))
        {
            return "POU";
        }

        if (ContainsKeyword(text, "FUNCTION"))
        {
            return "POU";
        }

        if (ContainsKeyword(text, "INTERFACE"))
        {
            return "INTERFACE";
        }

        if (ContainsKeyword(text, "TYPE") || ContainsKeyword(text, "STRUCT")
            || ContainsKeyword(text, "UNION") || ContainsKeyword(text, "ENUM"))
        {
            return "DUT";
        }

        if (ContainsKeyword(text, "VAR_GLOBAL"))
        {
            return "GVL";
        }

        if (ContainsKeyword(text, "METHOD"))
        {
            return "METHOD";
        }

        if (ContainsKeyword(text, "PROPERTY"))
        {
            return "PROPERTY";
        }

        if (ContainsKeyword(text, "ACTION"))
        {
            return "ACTION";
        }

        return subType switch
        {
            602 or 603 or 604 => "POU",
            605 or 606 or 607 or 623 => "DUT",
            608 => "ACTION",
            609 => "METHOD",
            611 => "PROPERTY",
            615 => "GVL",
            618 => "INTERFACE",
            _ => "POU",
        };
    }

    private static bool ContainsKeyword(string text, string keyword)
    {
        foreach (var raw in text.Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            return line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static Dictionary<string, string> Skip(string path, string reason, string detail) =>
        new() { ["path"] = path, ["reason"] = reason, ["detail"] = detail };

    private static void Record(
        List<Dictionary<string, object?>> objects,
        string guid, string name, string type, string form, string path, string hash,
        string nhash)
    {
        objects.Add(new Dictionary<string, object?>
        {
            ["guid"] = guid,
            ["name"] = name,
            ["type"] = type,
            ["form"] = form,
            ["path"] = path.Replace('\\', '/'),
            ["hash"] = hash,
            ["nhash"] = nhash,
            ["parent"] = "",
        });
    }

    private static string RelTo(string file)
    {
        var full = Path.GetFullPath(file);
        var root = _outDir
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].Replace('\\', '/')
            : full.Replace('\\', '/');
    }

    private static string HashTextFile(string path) => HashText(File.ReadAllText(path));

    private static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return "sha256:" + sb;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
