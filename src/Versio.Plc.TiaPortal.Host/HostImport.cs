using System.Collections;
using System.Reflection;
using System.Text.Json;

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
            .Where(m => m.Name == "GenerateBlocksFromSource")
            .ToList();

        // V21 documents the zero-argument call as overwriting existing blocks.
        // GenerateBlockOptions is None | KeepOnError — KeepOnError is what created
        // BoolToDigVal_1 beside the live block. Do not fall through to it.
        TargetInvocationException? last = null;
        foreach (var method in methods.Where(m => m.GetParameters().Length == 0))
        {
            try
            {
                method.Invoke(source, null);
                return;
            }
            catch (TargetInvocationException ex)
            {
                last = ex;
            }
        }

        if (last is not null)
        {
            throw last.InnerException ?? last;
        }

        throw new InvalidOperationException(
            "GenerateBlocksFromSource() is missing on " + source.GetType().Name);
    }

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
