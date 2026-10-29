using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Versio.Plc.TiaPortal.Host;

internal static partial class HostApp
{
    private static string _outDir = "";

    /// <summary>What the last export recorded, so an object Portal refuses this time keeps its
    /// entry as well as its file (<see cref="KeepPrevious"/>). Read before anything is written.</summary>
    private static List<Dictionary<string, object?>> _previous = [];

    internal static void Export(object project, string outDir)
    {
        _outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        _previous = ReadPreviousManifest(Path.Combine(outDir, "_manifest.json"));
        var objects = new List<Dictionary<string, object?>>();
        var skipped = new List<Dictionary<string, string>>();
        // Same record shape versio_export.py writes and ProjectManifest.Parse reads.
        // A list of strings used to discard every warning (CleaningStation, 2026-08-19)
        // and log "manifest not fully readable" once per row.
        var warnings = new List<Dictionary<string, string>>();

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

        // Comment SetAttribute has been measured (hwprobe 2026-08-20). PlugNew has
        // not been round-tripped. YAML write must not gate the PLC walk: a failed
        // station dump still leaves SCL/UDT/tags in the mirror.
        try
        {
            ExportHardware(project, objects, warnings);
        }
        catch (Exception ex)
        {
            Warn(warnings, "EXPORT_FAILED", "Hardware", Innermost(ex));
            Emit("warning", "msg", "device catalog: " + Innermost(ex));
        }

        skipped.Add(Skip("HMI", "SERVICE_OBJECT", "WinCC screens are a coverage skip"));
        skipped.Add(Skip("Safety", "SERVICE_OBJECT", "F-programs are a coverage skip"));
        skipped.Add(Skip("Technology", "SERVICE_OBJECT", "technology objects are a coverage skip"));

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
        md.AppendLine("Hardware comments are exported as *.devices.yaml (Comment SetAttribute only; never PlugNew).");
        md.AppendLine("HMI, safety programs and technology objects are coverage skips.");
        md.AppendLine("Know-how-protected blocks export for review only and are not imported.");
        md.AppendLine("SimaticML beside an SCL file is XML_REVIEW (G12) — never applied back.");
        md.AppendLine("Data blocks emit *.scl when GenerateSource succeeds (SCL_REVIEW; SimaticML stays the import).");
        File.WriteAllText(Path.Combine(outDir, "_coverage.md"), md.ToString(), new UTF8Encoding(false));
    }
    private static void WalkBlocks(
        PlcRef plc, string root,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<Dictionary<string, string>> warnings)
    {
        var group = GetProp(plc.Software, "BlockGroup");
        WalkBlockGroup(plc, group, Path.Combine(root, "Program blocks"), objects, skipped, warnings);
    }

    private static void WalkBlockGroup(
        PlcRef plc, object? group, string dir,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> skipped,
        List<Dictionary<string, string>> warnings)
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
        List<Dictionary<string, string>> warnings)
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
            skipped.Add(Skip(name, "SERVICE_OBJECT", "safety blocks are a coverage skip"));
            return;
        }

        // SimaticML UId is a document-local integer (CleaningStation: 26,661 of them,
        // most common "35"). The first export keyed every object as "0"; skipping 0
        // keyed them as "1". A rename is paired by content hash, not by this key.
        var guid = $"{plc.DeviceName}/{plc.PlcName}/{typeName}/{name}#{number}";
        var xmlPath = Path.Combine(dir, Sanitize(name) + ".xml");
        try
        {
            ExportXml(block, xmlPath);
        }
        catch (Exception ex)
        {
            Warn(warnings, "XML_EXPORT_FAILED", RelTo(xmlPath), Innermost(ex));
            skipped.Add(Skip(name, "EXPORT_FAILED", Innermost(ex)));
            KeepPrevious(objects, xmlPath, guid, Innermost(ex));
            return;
        }

        ScrubExportedFile(xmlPath);
        var xml = File.Exists(xmlPath) ? File.ReadAllText(xmlPath) : "";
        // Exact "SCL" missed the block after generate on the import gate: the in-session
        // GenerateSource still worked, the next export keyed off ProgrammingLanguage
        // and wrote no .scl. Treat any language that names SCL / Structured Text the
        // same, and still try GenerateSource when the property is blank.
        //
        // Data blocks report ProgrammingLanguage "DB", which is neither SCL nor blank.
        // CleaningStation's 24 DBs therefore never called GenerateSource and landed as
        // SimaticML only — the panel opened a thousand-line XML blob for a start-value
        // table. Try it for DBs too. The .scl is SCL_REVIEW (export-only): instance-DB
        // GenerateBlocksFromSource is unmeasured, and making property text importable
        // first emptied every accessor. Literal must match ExportCoverage.SclReviewForm.
        var sclPath = Path.Combine(dir, Sanitize(name) + ".scl");
        var wroteScl = false;
        var dataBlock = IsDataBlock(typeName, language);
        if (LooksLikeScl(language) || language.Length == 0 || dataBlock)
        {
            try
            {
                wroteScl = GenerateSource(plc, block, sclPath);
            }
            catch (Exception ex)
            {
                Warn(warnings, "SCL_EXPORT_FAILED", RelTo(sclPath), Innermost(ex));
            }
        }

        if (wroteScl)
        {
            if (dataBlock)
            {
                Record(objects, guid, name, typeName, "SCL_REVIEW", RelTo(sclPath), HashTextFile(sclPath));
                Record(objects, guid, name, typeName, "NATIVE", RelTo(xmlPath), HashFile(xmlPath),
                    nhash: HashNormalized(xml));
            }
            else
            {
                Record(objects, guid, name, typeName, "SCL", RelTo(sclPath), HashTextFile(sclPath));
                Record(objects, guid, name, typeName, "XML_REVIEW", RelTo(xmlPath), HashFile(xmlPath),
                    nhash: HashNormalized(xml));
            }

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
        List<Dictionary<string, string>> warnings)
    {
        _ = skipped;
        var group = GetProp(plc.Software, "TypeGroup");
        WalkTypeGroup(plc, group, Path.Combine(root, "PLC data types"), objects, warnings);
    }

    private static void WalkTypeGroup(
        PlcRef plc, object? group, string dir,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> warnings)
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
            var guid = $"{plc.DeviceName}/{plc.PlcName}/UDT/{name}";
            try
            {
                ExportXml(type, xmlPath);
            }
            catch (Exception ex)
            {
                // Its own catch, apart from the one below: only a refused Export leaves the
                // previous file in place, so only a refused Export carries its entry forward.
                Warn(warnings, "TYPE_EXPORT_FAILED", RelTo(xmlPath), Innermost(ex));
                KeepPrevious(objects, xmlPath, guid, Innermost(ex));
                continue;
            }

            try
            {
                ScrubExportedFile(xmlPath);
                var xml = File.ReadAllText(xmlPath);
                var wrote = false;
                try
                {
                    wrote = GenerateSource(plc, type, udtPath);
                }
                catch (Exception ex)
                {
                    Warn(warnings, "UDT_SOURCE_FAILED", RelTo(udtPath), Innermost(ex));
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
                Warn(warnings, "TYPE_EXPORT_FAILED", RelTo(xmlPath), Innermost(ex));
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
        List<Dictionary<string, string>> warnings)
    {
        _ = skipped;
        var group = GetProp(plc.Software, "TagTableGroup");
        WalkTagGroup(plc, group, Path.Combine(root, "PLC tags"), objects, warnings);
    }

    private static void WalkTagGroup(
        PlcRef plc, object? group, string dir,
        List<Dictionary<string, object?>> objects,
        List<Dictionary<string, string>> warnings)
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
            var guid = $"{plc.DeviceName}/{plc.PlcName}/TAG_TABLE/{name}";
            try
            {
                ExportXml(table, xmlPath);
            }
            catch (Exception ex)
            {
                Warn(warnings, "TAG_TABLE_EXPORT_FAILED", RelTo(xmlPath), Innermost(ex));
                KeepPrevious(objects, xmlPath, guid, Innermost(ex));
                continue;
            }

            try
            {
                ScrubExportedFile(xmlPath);
                var xml = File.ReadAllText(xmlPath);
                Record(objects, guid, name, "TAG_TABLE", "NATIVE", RelTo(xmlPath), HashFile(xmlPath),
                    nhash: HashNormalized(xml));
                Emit("object", "path", RelTo(xmlPath), "guid", guid);
            }
            catch (Exception ex)
            {
                Warn(warnings, "TAG_TABLE_EXPORT_FAILED", RelTo(xmlPath), Innermost(ex));
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
        if (File.Exists(dest))
        {
            File.Delete(dest);
        }

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

    private static bool LooksLikeScl(string language) =>
        language.IndexOf("SCL", StringComparison.OrdinalIgnoreCase) >= 0
        || language.IndexOf("StructuredText", StringComparison.OrdinalIgnoreCase) >= 0
        || language.IndexOf("Structured Text", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsDataBlock(string typeName, string language) =>
        string.Equals(typeName, "DB", StringComparison.OrdinalIgnoreCase)
        || string.Equals(language, "DB", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name the previous export is kept under while Openness writes the new one.</summary>
    internal const string SetAsideSuffix = ".versio-previous";

    /// <summary>
    /// Export one object to <paramref name="dest"/>, and if Openness refuses, leave the
    /// previous export of it exactly where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Openness <c>Export(..., None)</c> refuses an existing file, and the app re-exports into
    /// the same tree the previous run left behind, so the old file has to be out of the way.
    /// Without that, CleaningStation's second export recorded 159 XML_EXPORT_FAILED warnings
    /// and zero objects (2026-08-19).
    /// </para>
    /// <para>
    /// <b>Out of the way is not gone.</b> Until 2026-10-29 the old file was deleted here, and
    /// when Export then threw there was nothing left. Portal refuses an inconsistent block —
    /// any block edited and not compiled since — so the file that went missing was the one
    /// block the owner had just changed, and git showed their work as a deletion while the
    /// project card said "in sync". The old file is set aside under
    /// <see cref="SetAsideSuffix"/>, put back if Export throws (a partial write is discarded),
    /// and deleted only once the new export exists. A host that dies between the two moves
    /// (Portal gone mid-Export, the job timed out) leaves the set-aside with no file beside
    /// it; the next run puts it back first, because it <i>is</i> the previous export, and
    /// then proceeds as usual — so a refusal on that run still has something to keep, and
    /// nothing is left behind as an untracked file the planner would skip as stray.
    /// </para>
    /// </remarks>
    internal static void ExportXml(object item, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!File.Exists(dest) && File.Exists(dest + SetAsideSuffix))
        {
            File.Move(dest + SetAsideSuffix, dest);
        }

        var setAside = File.Exists(dest) ? dest + SetAsideSuffix : null;
        if (setAside is not null)
        {
            File.Delete(setAside);
            File.Move(dest, setAside);
        }

        try
        {
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
        catch
        {
            if (setAside is not null)
            {
                File.Delete(dest);
                File.Move(setAside, dest);
            }

            throw;
        }

        if (setAside is not null)
        {
            File.Delete(setAside);
        }
    }

    /// <summary>
    /// An object Portal refused to export keeps what the last export left: the file, which
    /// <see cref="ExportXml"/> put back, and its manifest entries, so the mirror still knows
    /// the object. Said on stdout too, because the manifest's own lists reach no log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On 2026-10-29 the refusal ("Inconsistent blocks and PLC data types (UDT) cannot be
    /// exported") went into <c>_manifest.json</c>'s <c>skipped</c> and <c>warnings</c> lists
    /// and nowhere else: the app's runner reads only the stdout protocol, so the log had no
    /// WRN line, the coverage file said "Skipped: 4" where it had said 3, and the one block
    /// the owner had edited was gone from the mirror without a word.
    /// </para>
    /// <para>
    /// The entries carried forward are the previous ones, hash and all. They say what the kept
    /// file is, which is what the deletion check (<c>LiveMatchesRecorded</c>) needs in order to
    /// refuse deleting a live object that no longer matches its last export. An entry found by
    /// guid rather than by path is the object's other file (the <c>.scl</c> beside a block's
    /// XML); it is carried only while that file is still there. The kept files are not
    /// emitted as objects: the protocol's object list is what this export touched.
    /// </para>
    /// </remarks>
    private static void KeepPrevious(
        List<Dictionary<string, object?>> objects, string xmlPath, string guid, string detail)
    {
        var rel = RelTo(xmlPath);
        var kept = File.Exists(xmlPath);
        var carried = kept
            ? PreviousEntriesFor(_previous, rel, guid)
                .Where(e => e.TryGetValue("path", out var v) && v is string p
                            && File.Exists(Path.Combine(_outDir, p.Replace('/', Path.DirectorySeparatorChar))))
                .ToList()
            : [];
        objects.AddRange(carried);

        var fate = !kept ? "there is no earlier export of it to keep"
            : carried.Count == 0 ? "its earlier export is kept, though the last manifest did not record it"
            : "its earlier export is kept as it was";
        Emit("warning", "msg", $"{rel}: Portal refused to export it ({Regex.Replace(detail, @"\s+", " ").Trim()}); {fate}");
    }

    /// <summary>The last manifest's entries for one object: by its path, or by its identity.</summary>
    internal static List<Dictionary<string, object?>> PreviousEntriesFor(
        IEnumerable<Dictionary<string, object?>> previous, string path, string guid) =>
        previous
            .Where(e => (e.TryGetValue("path", out var p) && string.Equals(p as string, path, StringComparison.Ordinal))
                     || (e.TryGetValue("guid", out var g) && string.Equals(g as string, guid, StringComparison.Ordinal)))
            .ToList();

    /// <summary>
    /// The objects the last export recorded, as the rows this export writes; empty when there
    /// is no manifest yet or it cannot be read.
    /// </summary>
    /// <remarks>
    /// Every value in a row is a string (see <see cref="Record"/>), so the rows round-trip
    /// without a schema. An unreadable manifest is said on stdout and treated as none: the
    /// file of a refused object is still kept, only its entry is not.
    /// </remarks>
    internal static List<Dictionary<string, object?>> ReadPreviousManifest(string manifestPath)
    {
        var entries = new List<Dictionary<string, object?>>();
        if (!File.Exists(manifestPath))
        {
            return entries;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("objects", out var objects)
                || objects.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (var row in objects.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entry = new Dictionary<string, object?>();
                foreach (var property in row.EnumerateObject())
                {
                    entry[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.ToString();
                }

                entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            Emit("warning", "msg",
                $"the previous _manifest.json could not be read ({Innermost(ex)}); "
                + "an object Portal refuses this run keeps its file but not its entry");
        }

        return entries;
    }

    private static readonly Regex ExportStamp = new(
        @"^[ \t]*<Created>[^<]*</Created>[ \t]*\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The export without Portal's <c>&lt;Created&gt;</c> line: the time of the export, in
    /// every file, every time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing else in the mirror says when it was made, and this line is why a second export
    /// of an untouched project changed 1,354 of 1,359 files on 2026-10-29 — one line each — and
    /// why the Changed count on the project card counted files rather than edits. The line
    /// goes, and only the line: the rest of the file stays Portal's bytes (CRLF, the BOM, the
    /// product list), so a diff between two exports is what changed in the project.
    /// </para>
    /// <para>
    /// <see cref="HashNormalized"/> drops the same element, so a live object's in-session export
    /// (which has the line) still compares with what was recorded (which has not), and the
    /// deletion check keeps working. <c>DocumentInfo</c> is export information; an import of a
    /// file without this line has not been measured against Portal — see the vendor notes.
    /// </para>
    /// <para>
    /// Only inside <c>DocumentInfo</c>, the same scope as the two hashes. CleaningStation's 159
    /// files carry one <c>Created</c> each, all in the header, so nothing else matches today;
    /// the scope is so that a block attribute of that name, should a release add one, is content.
    /// </para>
    /// </remarks>
    internal static string StripExportStamp(string xml)
    {
        var start = xml.IndexOf("<DocumentInfo>", StringComparison.Ordinal);
        var end = start < 0 ? -1 : xml.IndexOf("</DocumentInfo>", start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            return xml;
        }

        var header = xml.Substring(start, end - start);
        var stripped = ExportStamp.Replace(header, string.Empty);
        return ReferenceEquals(stripped, header) ? xml : xml.Substring(0, start) + stripped + xml.Substring(end);
    }

    /// <summary>Rewrite an exported file without its stamp, keeping its BOM and line endings.</summary>
    internal static void ScrubExportedFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var stripped = StripExportStamp(text);
        if (string.Equals(stripped, text, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, stripped, new UTF8Encoding(bom));
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

    private static void Warn(
        List<Dictionary<string, string>> warnings, string code, string path, string detail) =>
        warnings.Add(new() { ["code"] = code, ["path"] = path, ["detail"] = detail });

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

    internal static string HashNormalized(string xml)
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

            // The export stamp is an element, not an attribute. The written file has it
            // stripped (StripExportStamp); an in-session export has it; both must hash alike.
            foreach (var created in doc.Descendants()
                         .Where(e => e.Name.LocalName == "Created" && e.Parent?.Name.LocalName == "DocumentInfo")
                         .ToList())
            {
                created.Remove();
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
