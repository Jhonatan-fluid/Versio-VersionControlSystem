using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Openness session: probe, export, import. Types are loaded by name so this project
/// compiles without Siemens DLLs on the machine that builds Versio.
/// </summary>
internal static class HostApp
{
    // System.Text.Json and ContentHash.Of both reject a BOM. The first export wrote
    // _manifest.json with one and the analyser could not even parse it.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static int Run(string json)
    {
        var job = JobReader.Parse(json);
        Emit("info", "op", job.Op, "project", job.Project ?? "");

        object? portal = null;
        object? project = null;
        try
        {
            portal = CreatePortal();
            if (string.IsNullOrWhiteSpace(job.Project) || !File.Exists(job.Project))
            {
                EmitFatal("project file is missing: " + job.Project);
                return 3;
            }

            project = OpenProject(portal, job.Project!);
            if (project is null)
            {
                EmitFatal("Projects.Open returned nothing. The project may be held by another " +
                          "TIA Portal instance, or this user is not in the Siemens TIA Openness group.");
                return 4;
            }

            var op = job.Op.ToLowerInvariant();
            if (op == "probe")
            {
                Probe(project, job.Out);
            }
            else if (op == "export")
            {
                if (string.IsNullOrWhiteSpace(job.Out))
                {
                    EmitFatal("export job has no out directory");
                    return 5;
                }

                Export(project, job.Out!);
            }
            else if (op == "import")
            {
                if (string.IsNullOrWhiteSpace(job.Src))
                {
                    EmitFatal("import job has no src directory");
                    return 5;
                }

                Import(project, job.Src!, job.Plan, job.Save);
            }
            else
            {
                EmitFatal("unknown op: " + job.Op);
                return 6;
            }

            EmitDone();
            return 0;
        }
        catch (Exception ex)
        {
            EmitFatal(Innermost(ex));
            return 1;
        }
        finally
        {
            TryDispose(project);
            TryDispose(portal);
        }
    }

    private static object CreatePortal()
    {
        var tiaType = FindType("Siemens.Engineering.TiaPortal")
                      ?? throw new InvalidOperationException(
                          "Siemens.Engineering.TiaPortal was not found. Check the PublicAPI folder.");
        var modeType = FindType("Siemens.Engineering.TiaPortalMode")
                       ?? throw new InvalidOperationException("TiaPortalMode missing");
        var withoutUi = Enum.Parse(modeType, "WithoutUserInterface");
        return Activator.CreateInstance(tiaType, withoutUi)
               ?? throw new InvalidOperationException("failed to construct TiaPortal");
    }

    private static object? OpenProject(object portal, string path)
    {
        var projects = portal.GetType().GetProperty("Projects")?.GetValue(portal)
                       ?? throw new InvalidOperationException("TiaPortal.Projects missing");
        var open = projects.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "Open"
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType.Name == "FileInfo");
        if (open is null)
        {
            throw new InvalidOperationException("Projects.Open(FileInfo) missing — refusing OpenWithUpgrade");
        }

        return open.Invoke(projects, [new FileInfo(path)]);
    }

    private static void Probe(object project, string? outDir)
    {
        var report = new Dictionary<string, object?>
        {
            ["project"] = Str(project, "Name") ?? Str(project, "Path"),
            ["devices"] = new List<object>(),
        };
        var devices = (List<object>)report["devices"]!;

        foreach (var plc in WalkPlcSoftware(project))
        {
            var blocks = Count(plc.Software, "BlockGroup", "Blocks", "Groups");
            devices.Add(new Dictionary<string, object?>
            {
                ["device"] = plc.DeviceName,
                ["plc"] = plc.PlcName,
                ["blocks"] = blocks,
            });
            Emit("object", "path", plc.DeviceName + "/" + plc.PlcName, "type", "PLC");
        }

        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outDir, "report.json"), json, Utf8NoBom);
        }
    }

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
            var root = Path.Combine(outDir, Sanitize(plc.DeviceName), Sanitize(plc.PlcName));
            Directory.CreateDirectory(root);

            WalkBlocks(plc, root, objects, skipped, warnings);
            WalkTypes(plc, root, objects, skipped, warnings);
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

    private static void Import(object project, string srcDir, JsonElement plan, bool save)
    {
        if (plan.ValueKind != JsonValueKind.Object)
        {
            EmitFatal("import job has no plan");
            return;
        }

        var files = plan.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().ToList()
            : [];
        var deletions = plan.TryGetProperty("deletions", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().ToList()
            : [];
        var renames = plan.TryGetProperty("renames", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().ToList()
            : [];

        var plcs = WalkPlcSoftware(project).ToList();
        if (plcs.Count == 0)
        {
            EmitFatal("no PLC software found in the project");
            return;
        }

        foreach (var rename in renames)
        {
            var guid = Str(rename, "guid");
            var newName = Str(rename, "newName");
            if (guid is null || newName is null)
            {
                continue;
            }

            if (FindBlock(plcs, guid) is { } block)
            {
                SetProp(block, "Name", newName);
                Emit("object", "path", newName, "action", "rename");
            }
        }

        foreach (var file in files)
        {
            var action = Str(file, "action") ?? "";
            var rel = Str(file, "path") ?? "";
            var abs = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (action.Equals("skip", StringComparison.OrdinalIgnoreCase)
                || action.Equals("Skip", StringComparison.Ordinal))
            {
                continue;
            }

            if (!File.Exists(abs))
            {
                Emit("warning", "msg", "missing file " + rel);
                continue;
            }

            var plc = MatchPlc(plcs, Str(file, "resolve"), rel) ?? plcs[0];
            if (action is "text" or "ApplyText" or "create" or "Create")
            {
                ApplyScl(plc, abs);
                Emit("object", "path", rel, "action", action);
            }
            else if (action is "native" or "ApplyNative")
            {
                ApplyNative(plc, abs);
                Emit("object", "path", rel, "action", action);
            }
        }

        foreach (var del in deletions)
        {
            if (del.TryGetProperty("cannotVerify", out var cv)
                && cv.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(cv.GetString()))
            {
                Emit("warning", "msg", "kept " + Str(del, "name") + ": " + cv.GetString());
                continue;
            }

            var guid = Str(del, "guid");
            if (guid is null)
            {
                continue;
            }

            if (FindBlock(plcs, guid) is { } block)
            {
                Call(block, "Delete");
                Emit("object", "path", Str(del, "name") ?? guid, "action", "delete");
            }
        }

        if (save)
        {
            Call(project, "Save");
        }
    }

    private static void ApplyScl(PlcRef plc, string path)
    {
        var group = GetProp(plc.Software, "ExternalSourceGroup");
        if (group is null)
        {
            throw new InvalidOperationException("PlcSoftware.ExternalSourceGroup missing");
        }

        var name = Path.GetFileName(path);
        var existing = FindNamed(GetProp(group, "ExternalSources"), name);
        existing ??= FindNamed(GetEnumerable(group, "Sources"), name);
        if (existing is not null)
        {
            Call(existing, "Delete");
        }

        var source = CreateExternalSource(group, name, path);
        if (source is null)
        {
            throw new InvalidOperationException("could not create external source from " + path);
        }

        InvokeGenerateBlocks(source);
    }

    private static object? CreateExternalSource(object group, string name, string path)
    {
        var file = new FileInfo(path);
        var sources = GetProp(group, "ExternalSources") ?? group;
        foreach (var target in new[] { sources, group })
        {
            foreach (var method in target.GetType().GetMethods()
                         .Where(m => m.Name is "CreateFromFile" or "CreateFrom"))
            {
                var ps = method.GetParameters();
                try
                {
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                        && ps[1].ParameterType.Name == "FileInfo")
                    {
                        return method.Invoke(target, [name, file]);
                    }

                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                        && ps[1].ParameterType == typeof(string))
                    {
                        return method.Invoke(target, [name, path]);
                    }

                    if (ps.Length == 1 && ps[0].ParameterType.Name == "FileInfo")
                    {
                        return method.Invoke(target, [file]);
                    }
                }
                catch (TargetInvocationException)
                {
                    // Next overload.
                }
            }
        }

        return null;
    }

    private static void InvokeGenerateBlocks(object source)
    {
        var methods = source.GetType().GetMethods()
            .Where(m => Has(m.Name, "GenerateBlock")
                        || m.Name == "GenerateBlocksFromSource")
            .ToList();
        foreach (var method in methods)
        {
            var ps = method.GetParameters();
            try
            {
                if (ps.Length == 0)
                {
                    method.Invoke(source, null);
                    return;
                }

                if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
                {
                    object? overwrite = null;
                    try
                    {
                        overwrite = Enum.Parse(ps[0].ParameterType, "Overwrite");
                    }
                    catch (ArgumentException)
                    {
                        overwrite = Enum.ToObject(ps[0].ParameterType, 1);
                    }

                    method.Invoke(source, [overwrite]);
                    return;
                }
            }
            catch (TargetInvocationException)
            {
                // Try the next overload — Openness versions disagree on the option enum.
            }
        }
    }

    private static void ApplyNative(PlcRef plc, string path)
    {
        var blocks = GetProp(GetProp(plc.Software, "BlockGroup"), "Blocks");
        if (blocks is null)
        {
            return;
        }

        var import = blocks.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "Import" && m.GetParameters().Length >= 2);
        if (import is null)
        {
            return;
        }

        var optsType = import.GetParameters()[1].ParameterType;
        var overrideOpt = Enum.Parse(optsType, "Override");
        import.Invoke(blocks, [new FileInfo(path), overrideOpt]);
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

    private static object? FindBlock(IReadOnlyList<PlcRef> plcs, string guid)
    {
        foreach (var plc in plcs)
        {
            foreach (var block in AllBlocks(GetProp(plc.Software, "BlockGroup")))
            {
                var name = Str(block, "Name") ?? "";
                var number = Str(block, "Number") ?? "";
                var typeName = BlockType(block);
                var identity = $"{plc.DeviceName}/{plc.PlcName}/{typeName}/{name}#{number}";
                if (string.Equals(identity, guid, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, guid, StringComparison.OrdinalIgnoreCase)
                    || guid.EndsWith("/" + name + "#" + number, StringComparison.OrdinalIgnoreCase)
                    || Has(guid, "/" + name + "#"))
                {
                    return block;
                }

                // Openness may still expose a composition id on the live object. Matching
                // it is harmless; recording SimaticML's document-local UId as the manifest
                // guid is what collapsed CleaningStation to a single identity.
                foreach (var prop in new[] { "UId", "Id", "ObjectId" })
                {
                    if (string.Equals(Str(block, prop), guid, StringComparison.OrdinalIgnoreCase))
                    {
                        return block;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<object> AllBlocks(object? group)
    {
        if (group is null)
        {
            yield break;
        }

        foreach (var block in Enumerate(GetProp(group, "Blocks")))
        {
            yield return block;
        }

        foreach (var child in Enumerate(GetProp(group, "Groups")))
        {
            foreach (var block in AllBlocks(child))
            {
                yield return block;
            }
        }
    }

    private static PlcRef? MatchPlc(IReadOnlyList<PlcRef> plcs, string? resolveJson, string rel)
    {
        var slash = rel.Replace('\\', '/').Split('/');
        if (slash.Length >= 2)
        {
            var hit = plcs.FirstOrDefault(p =>
                string.Equals(p.DeviceName, slash[0], StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.PlcName, slash[1], StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        _ = resolveJson;
        return plcs.Count == 1 ? plcs[0] : null;
    }

    private static string BlockType(object block)
    {
        var t = block.GetType().Name;
        if (Has(t, "OB") && !Has(t, "FB"))
        {
            return "OB";
        }

        if (Has(t, "FB"))
        {
            return "FB";
        }

        if (Has(t, "FC"))
        {
            return "FC";
        }

        if (Has(t, "DB") || Has(t, "DataBlock"))
        {
            return "DB";
        }

        return "FB";
    }

    private static bool Has(string hay, string needle) =>
        hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

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

    private static object? GetService(object provider, string typeName)
    {
        var serviceType = FindType(typeName);
        if (serviceType is null)
        {
            return null;
        }

        var method = provider.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 0);
        try
        {
            return method?.MakeGenericMethod(serviceType).Invoke(provider, null);
        }
        catch
        {
            return null;
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (t is not null)
            {
                return t;
            }
        }

        return null;
    }

    private static object? GetProp(object? target, string name) =>
        target?.GetType().GetProperty(name)?.GetValue(target);

    private static void SetProp(object target, string name, object value) =>
        target.GetType().GetProperty(name)?.SetValue(target, value);

    private static void Call(object target, string name) =>
        target.GetType().GetMethod(name, Type.EmptyTypes)?.Invoke(target, null);

    private static IEnumerable<object> Enumerate(object? composition)
    {
        if (composition is null)
        {
            yield break;
        }

        if (composition is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            yield break;
        }

        var getEnumerator = composition.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
        if (getEnumerator?.Invoke(composition, null) is IEnumerator en)
        {
            while (en.MoveNext())
            {
                if (en.Current is not null)
                {
                    yield return en.Current;
                }
            }
        }
    }

    private static object? FindNamed(object? composition, string name) =>
        Enumerate(composition).FirstOrDefault(o =>
            string.Equals(Str(o, "Name"), name, StringComparison.OrdinalIgnoreCase));

    private static object? GetEnumerable(object target, string name) => GetProp(target, name);

    private static string? Str(object target, string name) =>
        GetProp(target, name)?.ToString();

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    private static bool Bool(object target, string name)
    {
        var v = GetProp(target, name);
        return v is true;
    }

    private static void TryDispose(object? obj)
    {
        try
        {
            (obj as IDisposable)?.Dispose();
        }
        catch
        {
            // Closing TIA after a failed open still has to return a fatal we already emitted.
        }
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }

    private static void Emit(string t, params string[] kv)
    {
        var sb = new StringBuilder();
        sb.Append("{\"t\":\"").Append(t).Append('"');
        for (var i = 0; i + 1 < kv.Length; i += 2)
        {
            sb.Append(",\"").Append(kv[i]).Append("\":\"").Append(Esc(kv[i + 1])).Append('"');
        }

        sb.Append('}');
        Console.WriteLine("@@VERSIO@@" + sb);
    }

    private static void EmitFatal(string msg) => Emit("fatal", "msg", msg);

    private static void EmitDone() => Emit("done", "ok", "true");

    private static string Esc(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private sealed record PlcRef(string DeviceName, string PlcName, object Software, object DeviceItem);
}
