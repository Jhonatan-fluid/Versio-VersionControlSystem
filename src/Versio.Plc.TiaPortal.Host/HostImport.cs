using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Versio.Plc.TiaPortal.Host;

internal static partial class HostApp
{
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

            if (FindObject(plcs, guid) is { } found)
            {
                SetProp(found, "Name", newName);
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
                // Throws rather than emitting success: import used to report the path
                // in ChangedPaths after a generate that left the live block unchanged
                // or unexportable. A throw skips Save, so the file on disk is intact.
                ApplyScl(plc, abs);
                Emit("object", "path", rel, "action", action);
            }
            else if (action is "native" or "ApplyNative")
            {
                ApplyNative(plc, abs, Str(file, "guid"));
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

            if (FindObject(plcs, guid) is { } found)
            {
                if (TryDelete(found, Str(del, "name") ?? guid))
                {
                    Emit("object", "path", Str(del, "name") ?? guid, "action", "delete");
                }
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

        var applied = File.ReadAllText(path);
        var objectName = SclObjectName(applied) ?? Path.GetFileNameWithoutExtension(path);
        var isType = IsTypeSource(path, applied);
        var existing = FindSoftware(plc, objectName, isType);
        var before = existing is { } hit ? TryGenerateSourceText(plc, hit.Obj, isType) : null;

        var staged = StageSourceFile(path);
        var srcName = Path.GetFileName(path);
        try
        {
            var existingSource = FindNamed(GetProp(group, "ExternalSources"), srcName);
            existingSource ??= FindNamed(GetEnumerable(group, "Sources"), srcName);
            if (existingSource is not null)
            {
                Call(existingSource, "Delete");
            }

            var source = CreateExternalSource(group, srcName, staged);
            if (source is null)
            {
                throw new InvalidOperationException("could not create external source from " + path);
            }

            // Prefer the overload that names the destination group. Zero-arg overwrite
            // on V21 left Level unexportable on the next host process; KeepOnError
            // cloned BoolToDigVal_1. None is the other enum value.
            if (!TryGenerateBlocks(source, existing?.Group, isType))
            {
                throw new InvalidOperationException("GenerateBlocksFromSource did not run on " + srcName);
            }
        }
        finally
        {
            try
            {
                File.Delete(staged);
            }
            catch (IOException)
            {
                // Temp.
            }
        }

        var live = FindSoftware(plc, objectName, isType);
        var language = live is null ? "" : Str(live.Obj, "ProgrammingLanguage") ?? "";
        Emit("info", "msg",
            objectName + " after generate language=" + language
            + " before=" + (before is null ? "none" : "snapshot"));

        if (!LiveCarriesApplied(plc, objectName, isType, applied, before, expectScl: !isType))
        {
            throw new InvalidOperationException(
                "generate did not apply " + srcName + " onto " + objectName
                + " (language=" + language + ")");
        }
    }

    /// <summary>
    /// Siemens documents external sources as ASCII. GenerateSource writes UTF-8 with a
    /// BOM; CreateFromFile from a no-BOM edit is a different encoding than the one
    /// the block was exported with. Stage a BOM copy so generate sees the same shape.
    /// </summary>
    private static string StageSourceFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        var staged = Path.Combine(Path.GetTempPath(),
            "versio-tia-src-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            + Path.GetExtension(path));
        if (hasBom)
        {
            File.Copy(path, staged, overwrite: true);
        }
        else
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            using var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.Read);
            output.Write(bom, 0, 3);
            output.Write(raw, 0, raw.Length);
        }

        return staged;
    }

    /// <summary>
    /// Object exists, can emit source, every line the applied file added is in that
    /// source — and, when the applied file changed anything at all, the source has
    /// moved off its pre-generate text, so a deletion-only edit cannot no-op silently.
    /// </summary>
    private static bool LiveCarriesApplied(
        PlcRef plc, string objectName, bool isType, string applied, string? before,
        bool expectScl)
    {
        var live = FindSoftware(plc, objectName, isType);
        if (live is null)
        {
            Emit("info", "msg", objectName + " is not in the project after generate");
            return false;
        }

        var language = Str(live.Obj, "ProgrammingLanguage") ?? "";
        if (expectScl && language.Length > 0 && !LooksLikeScl(language))
        {
            Emit("info", "msg", objectName + " language after generate is " + language);
            return false;
        }

        TryCompile(live.Obj);
        var after = TryGenerateSourceText(plc, live.Obj, isType);
        if (string.IsNullOrWhiteSpace(after))
        {
            Emit("info", "msg", objectName + " cannot emit source after generate");
            return false;
        }

        var missing = NewLinesNotPresent(applied, before ?? "", after!);
        if (missing is not null)
        {
            Emit("warning", "msg", objectName + " source is missing applied line: " + missing);
            return false;
        }

        // An edit that only REMOVES lines adds nothing for the check above to look for,
        // so a generate that silently no-ops would still report success — and the next
        // export would rewrite the mirror from the unchanged object, resurrecting the
        // deleted line: a silent revert of the user's edit. If the source changed the
        // object, the object's own source must have moved off its old text. Compared
        // whitespace-collapsed, so a re-indent by generate is not a false refusal.
        if (before is not null
            && !CollapsedEquals(applied, before)
            && CollapsedEquals(after!, before))
        {
            Emit("warning", "msg", objectName + " is unchanged after generate (edit not applied)");
            return false;
        }

        var marker = after!.IndexOf("versio_mutate_", StringComparison.Ordinal) >= 0;
        Emit("info", "msg",
            objectName + " verify length=" + after.Length + " marker=" + marker
            + " language=" + (Str(live.Obj, "ProgrammingLanguage") ?? ""));
        return true;
    }

    private static string? NewLinesNotPresent(string applied, string? before, string after)
    {
        var beforeSet = new HashSet<string>(CollapsedLines(before ?? ""), StringComparer.Ordinal);
        var afterCollapsed = CollapseWhite(after);
        foreach (var line in CollapsedLines(applied))
        {
            if (beforeSet.Contains(line))
            {
                continue;
            }

            if (afterCollapsed.IndexOf(line, StringComparison.Ordinal) < 0)
            {
                return line;
            }
        }

        return null;
    }

    private static bool CollapsedEquals(string left, string right) =>
        string.Join("\n", CollapsedLines(left))
            .Equals(string.Join("\n", CollapsedLines(right)), StringComparison.Ordinal);

    private static IEnumerable<string> CollapsedLines(string text) =>
        text.Replace('\r', '\n').Split('\n')
            .Select(CollapseWhite)
            .Where(s => s.Length > 0);

    private static string CollapseWhite(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");

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

    private static bool TryGenerateBlocks(object source, object? parentGroup, bool isType)
    {
        var methods = source.GetType().GetMethods()
            .Where(m => m.Name == "GenerateBlocksFromSource")
            .ToList();

        if (parentGroup is not null)
        {
            foreach (var method in methods)
            {
                var ps = method.GetParameters();
                if (ps.Length != 2
                    || !ps[0].ParameterType.IsInstanceOfType(parentGroup)
                    || !ps[1].ParameterType.IsEnum)
                {
                    continue;
                }

                try
                {
                    var none = Enum.Parse(ps[1].ParameterType, "None");
                    method.Invoke(source, [parentGroup, none]);
                    Emit("info", "msg",
                        "GenerateBlocksFromSource(" + (isType ? "type-group" : "block-group") + ", None)");
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    Emit("warning", "msg",
                        "GenerateBlocksFromSource(group, None): " + Innermost(ex));
                }
            }
        }

        foreach (var method in methods.Where(m => m.GetParameters().Length == 0))
        {
            try
            {
                method.Invoke(source, null);
                Emit("info", "msg", "GenerateBlocksFromSource()");
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Emit("warning", "msg", "GenerateBlocksFromSource(): " + Innermost(ex));
            }
        }

        return false;
    }

    private static SoftwareHit? FindSoftware(PlcRef plc, string name, bool isType)
    {
        var root = GetProp(plc.Software, isType ? "TypeGroup" : "BlockGroup");
        var items = isType ? "Types" : "Blocks";
        foreach (var (item, group) in NamedWithGroup(root, items))
        {
            if (string.Equals(Str(item, "Name"), name, StringComparison.OrdinalIgnoreCase))
            {
                return new SoftwareHit(item, group);
            }
        }

        return null;
    }

    private static IEnumerable<(object Item, object Group)> NamedWithGroup(object? group, string items)
    {
        if (group is null)
        {
            yield break;
        }

        foreach (var item in Enumerate(GetProp(group, items)))
        {
            yield return (item, group);
        }

        foreach (var child in Enumerate(GetProp(group, "Groups")))
        {
            foreach (var nested in NamedWithGroup(child, items))
            {
                yield return nested;
            }
        }
    }

    private static string? TryGenerateSourceText(PlcRef plc, object item, bool isType)
    {
        // Openness validates the target extension: a type generates to .udt, a block
        // to .scl — the same split the export walk writes. Asking a UDT for .scl
        // refused every textual type import as "cannot emit source after generate".
        var dest = Path.Combine(Path.GetTempPath(),
            "versio-tia-gs-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            + (isType ? ".udt" : ".scl"));
        try
        {
            return GenerateSource(plc, item, dest) ? File.ReadAllText(dest) : null;
        }
        catch (Exception ex)
        {
            Emit("info", "msg", "GenerateSource: " + Innermost(ex));
            return null;
        }
        finally
        {
            try
            {
                File.Delete(dest);
            }
            catch (IOException)
            {
                // Temp.
            }
        }
    }

    private static void TryCompile(object obj)
    {
        object? compilable = obj;
        var viaService = GetService(obj, "Siemens.Engineering.Compiler.ICompilable");
        if (viaService is not null)
        {
            compilable = viaService;
        }

        MethodInfo? compile = null;
        foreach (var iface in compilable.GetType().GetInterfaces())
        {
            if (string.Equals(iface.Name, "ICompilable", StringComparison.Ordinal))
            {
                compile = iface.GetMethod("Compile", Type.EmptyTypes);
                break;
            }
        }

        compile ??= compilable.GetType().GetMethod("Compile", Type.EmptyTypes);
        if (compile is null)
        {
            return;
        }

        try
        {
            var result = compile.Invoke(compilable, null);
            var state = result is null ? "" : Str(result, "State") ?? "";
            var errors = result is null ? "" : Str(result, "ErrorCount") ?? "";
            if (!string.IsNullOrEmpty(state) || !string.IsNullOrEmpty(errors))
            {
                Emit("info", "msg", "compile " + state + " errors=" + errors);
            }
        }
        catch (TargetInvocationException ex)
        {
            Emit("warning", "msg", "compile: " + Innermost(ex));
        }
    }

    private static readonly Regex SclHeader = new(
        @"^\s*(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\s+""?([^\s"";:]+)""?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Same rule as <c>TiaPortalSclHeader</c>. Duplicated because this host is net48
    /// and cannot reference the net9 pack.
    /// </summary>
    private static string? SclObjectName(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            var match = SclHeader.Match(trimmed);
            return match.Success ? match.Groups[2].Value : null;
        }

        return null;
    }

    private static bool IsTypeSource(string path, string text)
    {
        if (path.EndsWith(".udt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private sealed record SoftwareHit(object Obj, object Group);

    private static void ApplyNative(PlcRef plc, string path, string? guid)
    {
        var (groupName, items) = NativeComposition(guid, path);
        var composition = GetProp(GetProp(plc.Software, groupName), items);
        if (composition is null)
        {
            Emit("warning", "msg", "kept " + path + ": no " + items + " composition");
            return;
        }

        var import = composition.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "Import" && m.GetParameters().Length >= 2);
        if (import is null)
        {
            Emit("warning", "msg",
                "kept " + path + ": Import is not available on " + composition.GetType().Name);
            return;
        }

        try
        {
            var optsType = import.GetParameters()[1].ParameterType;
            var overrideOpt = Enum.Parse(optsType, "Override");
            import.Invoke(composition, [new FileInfo(path), overrideOpt]);
        }
        catch (Exception ex)
        {
            // Same class of refusal as TryDelete: a missing or throwing Import is
            // not a second API to invent. The object stays; the textual sibling
            // (if any) was already applied.
            Emit("warning", "msg", "kept " + path + ": " + Innermost(ex));
        }
    }

    /// <summary>
    /// Which Openness composition a native payload belongs on.
    /// </summary>
    /// <remarks>
    /// Identity strings are the same ones export writes. Handing a tag table's
    /// SimaticML to <c>BlockGroup.Blocks.Import</c> fails with "out of context"
    /// — measured 2026-08-19 on the import gate. Types and tag tables used to
    /// be silent no-ops on find; they must not be silent mis-imports on apply.
    /// </remarks>
    private static (string Group, string Items) NativeComposition(string? guid, string path)
    {
        var hay = (guid ?? "") + "\n" + path.Replace('\\', '/');
        if (Has(hay, "/TAG_TABLE/") || Has(hay, "TAG_TABLE"))
        {
            return ("TagTableGroup", "TagTables");
        }

        if (Has(hay, "/UDT/"))
        {
            return ("TypeGroup", "Types");
        }

        return ("BlockGroup", "Blocks");
    }

    private static object? FindObject(IReadOnlyList<PlcRef> plcs, string guid)
    {
        foreach (var plc in plcs)
        {
            foreach (var block in AllBlocks(GetProp(plc.Software, "BlockGroup")))
            {
                if (MatchesBlock(plc, block, guid))
                {
                    return block;
                }
            }

            foreach (var type in AllNamed(GetProp(plc.Software, "TypeGroup"), "Types"))
            {
                var identity = $"{plc.DeviceName}/{plc.PlcName}/UDT/{Str(type, "Name")}";
                if (MatchesIdentity(type, guid, identity, Str(type, "Name") ?? ""))
                {
                    return type;
                }
            }

            foreach (var table in AllNamed(GetProp(plc.Software, "TagTableGroup"), "TagTables"))
            {
                var identity = $"{plc.DeviceName}/{plc.PlcName}/TAG_TABLE/{Str(table, "Name")}";
                if (MatchesIdentity(table, guid, identity, Str(table, "Name") ?? ""))
                {
                    return table;
                }
            }
        }

        return null;
    }

    private static bool MatchesBlock(PlcRef plc, object block, string guid)
    {
        var name = Str(block, "Name") ?? "";
        var number = Str(block, "Number") ?? "";
        var typeName = BlockType(block);
        var identity = $"{plc.DeviceName}/{plc.PlcName}/{typeName}/{name}#{number}";
        return MatchesIdentity(block, guid, identity, name)
               || guid.EndsWith("/" + name + "#" + number, StringComparison.OrdinalIgnoreCase)
               || Has(guid, "/" + name + "#");
    }

    private static bool MatchesIdentity(object obj, string guid, string identity, string name)
    {
        if (string.Equals(identity, guid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, guid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Openness may still expose a composition id on the live object. Matching
        // it is harmless; recording SimaticML's document-local UId as the manifest
        // guid is what collapsed CleaningStation to a single identity.
        foreach (var prop in new[] { "UId", "Id", "ObjectId" })
        {
            if (string.Equals(Str(obj, prop), guid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<object> AllNamed(object? group, string items)
    {
        if (group is null)
        {
            yield break;
        }

        foreach (var item in Enumerate(GetProp(group, items)))
        {
            yield return item;
        }

        foreach (var child in Enumerate(GetProp(group, "Groups")))
        {
            foreach (var nested in AllNamed(child, items))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Delete if Openness exposes it; otherwise keep the object and say why.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed: blocks have <c>Delete()</c>. Types and tag tables
    /// may not, or may throw. Guessing a second API is how an import would destroy
    /// the wrong thing. A missing method or a thrown Delete is the same class of
    /// refusal as an unverifiable delete — the object stays.
    /// </remarks>
    private static bool TryDelete(object obj, string name)
    {
        var method = obj.GetType().GetMethod("Delete", Type.EmptyTypes);
        if (method is null)
        {
            Emit("warning", "msg",
                "kept " + name + ": Delete is not available on " + obj.GetType().Name);
            return false;
        }

        try
        {
            method.Invoke(obj, null);
            return true;
        }
        catch (Exception ex)
        {
            Emit("warning", "msg", "kept " + name + ": " + Innermost(ex));
            return false;
        }
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
}
