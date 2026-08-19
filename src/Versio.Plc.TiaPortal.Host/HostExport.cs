using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Versio.Plc.TiaPortal.Host;

internal static partial class HostApp
{
    private static string _outDir = "";

    private static void Export(object project, string outDir)
    {
        _outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var objects = new List<Dictionary<string, object?>>();
        var skipped = new List<Dictionary<string, string>>();
        var warnings = new List<string>();

        foreach (var plc in WalkPlcSoftware(project))
        {
            Emit("progress", "phase", "export", "detail", plc.DeviceName + "/" + plc.PlcName);
            var root = Path.Combine(outDir, Sanitize(plc.DeviceName), Sanitize(plc.PlcName));
            Directory.CreateDirectory(root);

            WalkBlocks(plc, root, objects, skipped, warnings);
            Emit("progress", "phase", "types", "detail", plc.PlcName);
            WalkTypes(plc, root, objects, skipped, warnings);
            Emit("progress", "phase", "tags", "detail", plc.PlcName);
            WalkTags(plc, root, objects, skipped, warnings);
        }

        skipped.Add(Skip("Hardware", "NOT_IMPLEMENTED", "devices and networks are a coverage skip"));
        skipped.Add(Skip("HMI", "NOT_IMPLEMENTED", "WinCC screens are a coverage skip"));
        skipped.Add(Skip("Safety", "NOT_IMPLEMENTED", "F-programs are a coverage skip"));
        skipped.Add(Skip("Technology", "NOT_IMPLEMENTED", "technology objects are a coverage skip"));

        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 9,
            ["vendor"] = "TiaPortal",
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
        md.AppendLine("Hardware, HMI, safety programs and technology objects are coverage skips.");
        md.AppendLine("Know-how-protected blocks export for review only and are not imported.");
        md.AppendLine("SimaticML beside an SCL file is XML_REVIEW (G12) — never applied back.");
        File.WriteAllText(Path.Combine(outDir, "_coverage.md"), md.ToString(), new UTF8Encoding(false));
    }
    private static void WalkBlocks(
        PlcRef plc, string root,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<string> warnings)
    {
        var group = GetProp(plc.Software, "BlockGroup");
        WalkBlockGroup(plc, group, Path.Combine(root, "Program blocks"), objects, skipped, warnings);
    }

    private static void WalkBlockGroup(
        PlcRef plc, object? group, string dir,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<string> warnings)
    {
        if (group is null)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var block in Enumerate(GetProp(group, "Blocks")))
        {
            ExportBlock(plc, block, dir, objects, skipped, warnings);
        }

        foreach (var child in Enumerate(GetProp(group, "Groups")))
        {
            var name = Str(child, "Name") ?? "Group";
            WalkBlockGroup(plc, child, Path.Combine(dir, Sanitize(name)), objects, skipped, warnings);
        }
    }

    private static void ExportBlock(
        PlcRef plc, object block, string dir,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<string> warnings)
    {
        var name = Str(block, "Name") ?? "Block";
        var number = Str(block, "Number") ?? "0";
        var language = Str(block, "ProgrammingLanguage") ?? "";
        var protectedKnowHow = Bool(block, "IsKnowHowProtected");
        var typeName = BlockType(block);

        if (protectedKnowHow)
        {
            skipped.Add(Skip(name, "KNOW_HOW_PROTECTED", "know-how-protected blocks are not imported"));
            return;
        }

        if (language.StartsWith("F_", StringComparison.OrdinalIgnoreCase))
        {
            skipped.Add(Skip(name, "NOT_IMPLEMENTED", "safety blocks are a coverage skip"));
            return;
        }

        var xmlPath = Path.Combine(dir, Sanitize(name) + ".xml");
        try
        {
            ExportXml(block, xmlPath);
        }
        catch (Exception ex)
        {
            warnings.Add(name + ": XML export failed: " + Innermost(ex));
            skipped.Add(Skip(name, "EXPORT_FAILED", Innermost(ex)));
            return;
        }

        var xml = File.Exists(xmlPath) ? File.ReadAllText(xmlPath) : "";
        // SimaticML UId is a document-local integer (CleaningStation: 26,661 of them,
        // most common "35"). The first export keyed every object as "0"; skipping 0
        // keyed them as "1". A rename is paired by content hash, not by this key.
        var guid = $"{plc.DeviceName}/{plc.PlcName}/{typeName}/{name}#{number}";
        var isScl = language.Equals("SCL", StringComparison.OrdinalIgnoreCase);
        var sclPath = Path.Combine(dir, Sanitize(name) + ".scl");
        var wroteScl = false;

        if (isScl)
        {
            try
            {
                wroteScl = GenerateSource(plc, block, sclPath);
            }
            catch (Exception ex)
            {
                warnings.Add(name + ": SCL export failed: " + Innermost(ex));
            }
        }

        if (wroteScl)
        {
            Record(objects, guid, name, typeName, "SCL", RelTo(sclPath), HashTextFile(sclPath));
            Record(objects, guid, name, typeName, "XML_REVIEW", RelTo(xmlPath), HashFile(xmlPath),
                nhash: HashNormalized(xml));
            Emit("object", "path", RelTo(sclPath), "guid", guid);
        }
        else
        {
            Record(objects, guid, name, typeName, "NATIVE", RelTo(xmlPath), HashFile(xmlPath),
                nhash: HashNormalized(xml));
            Emit("object", "path", RelTo(xmlPath), "guid", guid);
        }
    }

    private static void WalkTypes(
        PlcRef plc, string root,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<string> warnings)
    {
        _ = skipped;
        var group = GetProp(plc.Software, "TypeGroup");
        WalkTypeGroup(plc, group, Path.Combine(root, "PLC data types"), objects, warnings);
    }

    private static void WalkTypeGroup(
        PlcRef plc, object? group, string dir,
        List<Dictionary<string, object?>> objects,
        List<string> warnings)
    {
        if (group is null)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var type in Enumerate(GetProp(group, "Types")))
        {
            var name = Str(type, "Name") ?? "Type";
            var xmlPath = Path.Combine(dir, Sanitize(name) + ".xml");
            var udtPath = Path.Combine(dir, Sanitize(name) + ".udt");
            try
            {
                ExportXml(type, xmlPath);
                var xml = File.ReadAllText(xmlPath);
                var guid = $"{plc.DeviceName}/{plc.PlcName}/UDT/{name}";
                var wrote = false;
                try
                {
                    wrote = GenerateSource(plc, type, udtPath);
                }
                catch (Exception ex)
                {
                    warnings.Add(name + ": UDT source failed: " + Innermost(ex));
                }

                if (wrote)
                {
                    Record(objects, guid, name, "UDT", "SCL", RelTo(udtPath), HashTextFile(udtPath));
                    Record(objects, guid, name, "UDT", "XML_REVIEW", RelTo(xmlPath), HashFile(xmlPath),
                        nhash: HashNormalized(xml));
                    Emit("object", "path", RelTo(udtPath), "guid", guid);
                }
                else
                {
                    Record(objects, guid, name, "UDT", "NATIVE", RelTo(xmlPath), HashFile(xmlPath),
                        nhash: HashNormalized(xml));
                    Emit("object", "path", RelTo(xmlPath), "guid", guid);
                }
            }
            catch (Exception ex)
            {
                warnings.Add(name + ": type export failed: " + Innermost(ex));
            }
        }

        foreach (var child in Enumerate(GetProp(group, "Groups")))
        {
            var name = Str(child, "Name") ?? "Group";
            WalkTypeGroup(plc, child, Path.Combine(dir, Sanitize(name)), objects, warnings);
        }
    }

    private static void WalkTags(
        PlcRef plc, string root,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<string> warnings)
    {
        _ = skipped;
        var group = GetProp(plc.Software, "TagTableGroup");
        WalkTagGroup(plc, group, Path.Combine(root, "PLC tags"), objects, warnings);
    }

    private static void WalkTagGroup(
        PlcRef plc, object? group, string dir,
        List<Dictionary<string, object?>> objects,
        List<string> warnings)
    {
        if (group is null)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var table in Enumerate(GetProp(group, "TagTables")))
        {
            var name = Str(table, "Name") ?? "Tags";
            var xmlPath = Path.Combine(dir, Sanitize(name) + ".xml");
            try
            {
                ExportXml(table, xmlPath);
                var xml = File.ReadAllText(xmlPath);
                var guid = $"{plc.DeviceName}/{plc.PlcName}/TAG_TABLE/{name}";
                Record(objects, guid, name, "TAG_TABLE", "NATIVE", RelTo(xmlPath), HashFile(xmlPath),
                    nhash: HashNormalized(xml));
                Emit("object", "path", RelTo(xmlPath), "guid", guid);
            }
            catch (Exception ex)
            {
                warnings.Add(name + ": tag table export failed: " + Innermost(ex));
            }
        }

        foreach (var child in Enumerate(GetProp(group, "Groups")))
        {
            var name = Str(child, "Name") ?? "Group";
            WalkTagGroup(plc, child, Path.Combine(dir, Sanitize(name)), objects, warnings);
        }
    }

    private static bool GenerateSource(PlcRef plc, object item, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var file = new FileInfo(dest);

        // V21 exposes IGenerateSource.GenerateSource(FileInfo) on the block itself.
        // Prefer that: a List<object> cannot satisfy IEnumerable<PlcBlock>.
        foreach (var method in item.GetType().GetMethods().Where(m => m.Name == "GenerateSource"))
        {
            var ps = method.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType.Name == "FileInfo")
            {
                try
                {
                    method.Invoke(item, [file]);
                    return File.Exists(dest) && file.Length > 0;
                }
                catch (TargetInvocationException)
                {
                    // Fall through to ExternalSourceGroup.
                }
            }
        }

        var group = GetProp(plc.Software, "ExternalSourceGroup");
        if (group is null)
        {
            return false;
        }

        foreach (var method in group.GetType().GetMethods().Where(m => m.Name == "GenerateSource"))
        {
            var ps = method.GetParameters();
            if (ps.Length < 2)
            {
                continue;
            }

            var first = ps[0].ParameterType;
            object? collection = item;
            if (first.IsGenericType)
            {
                var element = first.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                list.Add(item);
                collection = list;
            }

            try
            {
                var args = ps.Length == 2 ? new[] { collection, file } : new[] { collection, file, null };
                method.Invoke(group, args);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Next overload.
            }
        }

        return File.Exists(dest) && new FileInfo(dest).Length > 0;
    }

    private static void ExportXml(object item, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var export = item.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "Export" && m.GetParameters().Length == 2);
        if (export is null)
        {
            throw new InvalidOperationException("Export(FileInfo, ExportOptions) missing on " + item.GetType().Name);
        }

        var optsType = export.GetParameters()[1].ParameterType;
        var none = Enum.Parse(optsType, "None");
        export.Invoke(item, [new FileInfo(dest), none]);
    }

    private static IEnumerable<PlcRef> WalkPlcSoftware(object project)
    {
        foreach (var device in Enumerate(GetProp(project, "Devices")))
        {
            var deviceName = Str(device, "Name") ?? "Device";
            foreach (var item in WalkDeviceItems(device))
            {
                var container = GetService(item, "Siemens.Engineering.HW.Features.SoftwareContainer");
                var software = container is null ? null : GetProp(container, "Software");
                if (software is null)
                {
                    continue;
                }

                if (software.GetType().Name is not "PlcSoftware" and not "PlcSoftwareImpl")
                {
                    continue;
                }

                yield return new PlcRef(deviceName, Str(software, "Name") ?? deviceName, software, item);
            }
        }
    }

    private static IEnumerable<object> WalkDeviceItems(object device)
    {
        yield return device;
        foreach (var item in Enumerate(GetProp(device, "DeviceItems")))
        {
            foreach (var nested in WalkDeviceItems(item))
            {
                yield return nested;
            }
        }
    }

    private static Dictionary<string, string> Skip(string path, string reason, string detail) =>
        new() { ["path"] = path, ["reason"] = reason, ["detail"] = detail };

    private static void Record(
        List<Dictionary<string, object?>> objects,
        string guid, string name, string type, string form, string path, string hash,
        string nhash = "")
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
            ? full.Substring(root.Length).Replace('\\', '/')
            : full.Replace('\\', '/');
    }

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    /// <summary>
    /// Textual hashes must match <c>ContentHash.Of(text)</c> on the planner side: UTF-8
    /// without a BOM. Siemens GenerateSource writes a BOM; hashing the raw file would
    /// make every rename pairing miss.
    /// </summary>
    private static string HashTextFile(string path)
    {
        var text = File.ReadAllText(path);
        return HashBytes(Encoding.UTF8.GetBytes(text));
    }

    private static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return "sha256:" + sb;
    }

    // Same names the C# scrubber strips, so a deletion's nhash is comparable to a
    // re-export. Host cannot reference Versio.Core (net48 vs net9).
    private static readonly HashSet<string> ChurnAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DateModified", "DateCreated", "ModificationDate", "CreationDate",
            "Changed", "TimeStamp", "Timestamp", "Version",
        };

    private static string HashNormalized(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var el in doc.Descendants())
            {
                foreach (var attr in el.Attributes().ToList())
                {
                    if (ChurnAttributes.Contains(attr.Name.LocalName))
                    {
                        attr.Remove();
                    }
                }
            }

            return HashBytes(Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting)));
        }
        catch
        {
            return HashBytes(Encoding.UTF8.GetBytes(xml));
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static int Count(object software, string groupName, string items, string groups)
    {
        var n = 0;
        void Walk(object? g)
        {
            if (g is null)
            {
                return;
            }

            n += Enumerate(GetProp(g, items)).Count();
            foreach (var child in Enumerate(GetProp(g, groups)))
            {
                Walk(child);
            }
        }

        Walk(GetProp(software, groupName));
        return n;
    }
}
