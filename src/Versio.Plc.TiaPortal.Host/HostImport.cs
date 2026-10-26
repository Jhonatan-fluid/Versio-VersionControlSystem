using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Versio.Plc.TiaPortal.Host;

internal static partial class HostApp
{
    /// <summary>
    /// Apply a confirmed plan to the open project.
    /// </summary>
    /// <remarks>
    /// Internal, not private, so the pure tests — which compile this file in — can drive
    /// it against fakes shaped like the Openness objects it reads. Nothing else in the exe
    /// calls it but <see cref="Run"/>.
    /// </remarks>
    internal static void Import(object project, string srcDir, JsonElement plan, bool save)
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

        // Every file that targets a PLC must name a live one, checked BEFORE anything is
        // written — renames included, which run first. This used to be `MatchPlc(...) ??
        // plcs[0]` at the point of use: a plan for Device2/PLC2 whose device had been
        // renamed in Portal since the export wrote Device2's payload into Device1 and saved
        // (the fifth review's P1, reproduced against fakes in the pure tests). The whole plan
        // is refused rather than the one file, because a plan is one unit: applying the half
        // that resolves leaves the project in a state no plan described, and the remedy —
        // export again, re-plan — is the same either way. A fatal, not a warning, because
        // the runner turns a warning into "succeeded, with notes" and the lock moves to
        // ImportSucceeded; the mirror's edit would then sit unapplied until the next export
        // swept it away. Nothing has been written when this fires, so Save is not reached.
        var unresolved = new List<string>();
        foreach (var file in files)
        {
            var rel = Str(file, "path") ?? "";
            if (IsSkip(Str(file, "action") ?? "") || !TargetsPlc(rel))
            {
                continue;
            }

            if (MatchPlc(plcs, Str(file, "resolve"), rel) is null)
            {
                unresolved.Add(rel);
            }
        }

        if (unresolved.Count > 0)
        {
            EmitFatal(UnresolvedPlcRefusal(unresolved, plcs));
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
                SetProp(found.Item, "Name", newName);
                Emit("object", "path", newName, "action", "rename");
            }
            else
            {
                // Said out loud, because a silently skipped rename diverges the mirror:
                // the next export writes the OLD name back and the file that arrived
                // under the new name becomes a duplicate.
                Emit("warning", "msg",
                    "did not rename " + guid + ": no live object with that identity — "
                    + "it may have been renamed or moved in the IDE; export again");
            }
        }

        foreach (var file in files)
        {
            var action = Str(file, "action") ?? "";
            var rel = Str(file, "path") ?? "";
            var abs = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (IsSkip(action))
            {
                continue;
            }

            if (!File.Exists(abs))
            {
                Emit("warning", "msg", "missing file " + rel);
                continue;
            }

            if (!TargetsPlc(rel))
            {
                // A device catalog belongs to the project's stations, not to a PLC, which is
                // why the pre-flight above does not ask it to name one.
                if (action is "text" or "ApplyText" or "create" or "Create")
                {
                    ApplyDeviceComments(project, abs);
                    Emit("object", "path", rel, "action", action);
                }
                else
                {
                    // The planner never plans one, so this is a plan this host does not
                    // understand — said rather than skipped.
                    Emit("warning", "msg", "kept " + rel + ": a device catalog is applied as text, not as " + action);
                }

                continue;
            }

            // Never a fallback here: the pre-flight refused the plan if this could be null,
            // so a null now is a bug in that check, and throwing turns it into a fatal that
            // skips Save rather than a write into whichever PLC happens to be first.
            var plc = MatchPlc(plcs, Str(file, "resolve"), rel)
                      ?? throw new InvalidOperationException(
                          "no live PLC for " + rel + " — the plan should have been refused before any write");
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
            var name = Str(del, "name");
            if (guid is null)
            {
                Emit("warning", "msg",
                    "kept " + (name ?? "an object") + ": the plan carries no identity for it");
                continue;
            }

            if (FindObject(plcs, guid) is not { } found)
            {
                Emit("warning", "msg",
                    "kept " + (name ?? guid) + ": no live object with that identity — "
                    + "it may have been renamed or moved in the IDE; export again");
                continue;
            }

            // The contract on DeletedFile.Hash: the executing side compares the live object
            // against what the export recorded before removing anything. A mismatch means it
            // was edited in Portal since the export, and a missing mirror file is not
            // permission to destroy that edit. The CODESYS script (why_not_delete) and the
            // TwinCAT host (LiveMatchesRecorded) both make this check; until 2026-10-14 this
            // host checked only the plan-side cannotVerify and deleted on identity alone.
            if (!LiveMatchesRecorded(found, del, out var why))
            {
                Emit("warning", "msg", "kept " + (name ?? guid) + ": " + why);
                continue;
            }

            if (TryDelete(found.Item, name ?? guid))
            {
                Emit("object", "path", name ?? guid, "action", "delete");
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

    /// <summary>A live object a destructive action resolved, with where it lives.</summary>
    /// <param name="Plc">Needed by the delete check: GenerateSource runs through the PLC's
    /// ExternalSourceGroup, not through the object.</param>
    /// <param name="Kind"><c>block</c>, <c>type</c> or <c>table</c> — which comparisons the
    /// live object can answer (a tag table has no source to generate).</param>
    private sealed record FoundObject(PlcRef Plc, object Item, string Kind);

    /// <summary>
    /// The live object a plan's identity names, or null — for renames and deletes only.
    /// </summary>
    /// <remarks>
    /// <b>Exact identity only, and null is the safe answer.</b> Every caller is destructive
    /// (SetProp Name, TryDelete), and a miss is reported and kept — the remedy is an export.
    /// A fuzzy hit has no such recovery: delete raises no exception, no cascade and no
    /// warning (P3), so a wrong match is somebody's block gone. This used to accept
    /// <c>EndsWith("/" + name + "#" + number)</c> and a bare name-contains, and every S7 PLC
    /// has an OB named Main — so in a multi-PLC project a deletion planned for
    /// <c>Dev2/PLC_2/OB/Main#1</c> matched PLC_1's Main first in device order. The TwinCAT
    /// host is identity-only for the same reason and says so.
    /// </remarks>
    private static FoundObject? FindObject(IReadOnlyList<PlcRef> plcs, string guid)
    {
        foreach (var plc in plcs)
        {
            foreach (var block in AllBlocks(GetProp(plc.Software, "BlockGroup")))
            {
                if (MatchesBlock(plc, block, guid))
                {
                    return new FoundObject(plc, block, "block");
                }
            }

            foreach (var type in AllNamed(GetProp(plc.Software, "TypeGroup"), "Types"))
            {
                var identity = $"{plc.DeviceName}/{plc.PlcName}/UDT/{Str(type, "Name")}";
                if (MatchesIdentity(type, guid, identity))
                {
                    return new FoundObject(plc, type, "type");
                }
            }

            foreach (var table in AllNamed(GetProp(plc.Software, "TagTableGroup"), "TagTables"))
            {
                var identity = $"{plc.DeviceName}/{plc.PlcName}/TAG_TABLE/{Str(table, "Name")}";
                if (MatchesIdentity(table, guid, identity))
                {
                    return new FoundObject(plc, table, "table");
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
        return MatchesIdentity(block, guid, identity);
    }

    /// <summary>
    /// Does the live object still match what the last export recorded for it?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan's deleted files carry <c>hash</c> (the exported file's text/bytes) and
    /// <c>normalizedHash</c> (the SimaticML after churn attributes are stripped). The live
    /// side can answer with up to two values: GenerateSource of the block or type, hashed
    /// exactly as <c>HashTextFile</c> hashed the exported <c>.scl</c>/<c>.udt</c>, and a
    /// fresh in-session XML export through the same <c>HashNormalized</c> the export used
    /// for <c>nhash</c>. Any agreement means the object is what the mirror said it was.
    /// </para>
    /// <para>
    /// <b>Every failure answers "keep".</b> A live object that cannot be read back —
    /// GenerateSource refuses an inconsistent block, Export throws — is an object this
    /// session cannot prove unchanged, and deleting on an unprovable claim is the failure
    /// mode this check exists for. The raw XML byte hash is deliberately never computed
    /// live: native bytes churn between exports, which is why <c>nhash</c> exists, and a
    /// churned mismatch against the recorded raw hash would only ever widen "keep" — the
    /// normalized comparison is the honest one.
    /// </para>
    /// </remarks>
    private static bool LiveMatchesRecorded(FoundObject found, JsonElement del, out string why)
    {
        var recorded = new List<string>();
        if (del.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                foreach (var field in new[] { "hash", "normalizedHash" })
                {
                    if (Str(f, field) is { Length: > 0 } h)
                    {
                        recorded.Add(h);
                    }
                }
            }
        }

        if (recorded.Count == 0)
        {
            // The planner marks a hashless deletion CannotVerify, so reaching here means
            // the plan disagrees with its own precondition. Keep the object either way.
            why = "the plan records no hash to compare the live object against";
            return false;
        }

        var live = new List<string>();
        if (found.Kind is "block" or "type")
        {
            var text = TryGenerateSourceText(found.Plc, found.Item, isType: found.Kind == "type");
            if (text is not null)
            {
                live.Add(HashBytes(Encoding.UTF8.GetBytes(text)));
            }
        }

        if (TryExportXmlText(found.Item) is { } xml)
        {
            live.Add(HashNormalized(xml));
        }

        if (live.Count == 0)
        {
            why = "the live object could not be read back to compare against the last export";
            return false;
        }

        if (live.Any(recorded.Contains))
        {
            why = "";
            return true;
        }

        why = "the live object no longer matches the last export — it was edited in "
              + "Portal since, and a missing mirror file is not permission to destroy "
              + "that edit; export again";
        return false;
    }

    /// <summary>The live object's SimaticML, via a temp file, or null when Export refuses.</summary>
    private static string? TryExportXmlText(object item)
    {
        var dest = Path.Combine(Path.GetTempPath(),
            "versio-tia-del-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");
        try
        {
            ExportXml(item, dest);
            return File.Exists(dest) ? File.ReadAllText(dest) : null;
        }
        catch (Exception ex)
        {
            Emit("info", "msg", "Export for the delete check: " + Innermost(ex));
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

    private static bool MatchesIdentity(object obj, string guid, string identity)
    {
        if (string.Equals(identity, guid, StringComparison.OrdinalIgnoreCase))
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

    private static bool IsSkip(string action) =>
        action.Equals("skip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does this mirror file land on a PLC? Everything does except the device catalog,
    /// which <see cref="ApplyDeviceComments"/> applies to the project's stations.
    /// </summary>
    private static bool TargetsPlc(string rel) =>
        !rel.EndsWith(".devices.yaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The live PLC a mirror path belongs to, or null when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The export writes each PLC under <c>Sanitize(DeviceName)/Sanitize(PlcName)</c>, so the
    /// path's first two segments are compared against the <b>sanitised</b> names — a device
    /// called <c>Line:2</c> is the directory <c>Line_2</c>, and comparing the raw name would
    /// miss every file of that PLC. A name with nothing to sanitise is unchanged by it, so
    /// the plain case is covered too.
    /// </para>
    /// <para>
    /// <b>No fallback, not even with one PLC.</b> The first version of this kept one — "with
    /// a single PLC there is nowhere else a file could belong" — and that mistook the common
    /// case for the only case: one PLC is also what a project looks like after its other PLC
    /// was removed in Portal, and an old import for the removed Device2 then wrote Device2's
    /// blocks into Device1 and saved (the 2026-10-25 review's sixth round, item 2, P1; the
    /// fifth round's item 3 had closed the two-PLC case the same way). A path that names no
    /// live PLC is a miss however many PLCs remain, and the caller refuses the plan. A device
    /// renamed in a one-PLC project costs a re-export, which is the remedy the fatal already
    /// names; a block written into the wrong PLC costs the project.
    /// </para>
    /// </remarks>
    private static PlcRef? MatchPlc(IReadOnlyList<PlcRef> plcs, string? resolveJson, string rel)
    {
        var slash = rel.Replace('\\', '/').Split('/');
        if (slash.Length >= 2)
        {
            var hit = plcs.FirstOrDefault(p =>
                string.Equals(Sanitize(p.DeviceName), slash[0], StringComparison.OrdinalIgnoreCase)
                && string.Equals(Sanitize(p.PlcName), slash[1], StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        _ = resolveJson;
        return null;
    }

    /// <summary>
    /// The fatal's sentence: which targets are missing, which files wanted them, what is
    /// actually there, and what to do. One line, because the protocol is one event per line.
    /// </summary>
    private static string UnresolvedPlcRefusal(IReadOnlyList<string> unresolved, IReadOnlyList<PlcRef> plcs)
    {
        var byTarget = unresolved
            .GroupBy(rel =>
            {
                var slash = rel.Replace('\\', '/').Split('/');
                return slash.Length >= 2 ? slash[0] + "/" + slash[1] : rel;
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var sample = g.First();
                var more = g.Count() - 1;
                return g.Key + " (" + sample + (more > 0 ? ", +" + more + " more" : "") + ")";
            });

        var live = string.Join(", ", plcs.Select(p => p.DeviceName + "/" + p.PlcName));
        return "refusing the plan: " + unresolved.Count + " file(s) target a PLC that is not in "
               + "the project — " + string.Join("; ", byTarget) + "; live PLCs: " + live
               + ". A device or PLC was renamed or removed in the IDE since the export; "
               + "nothing was written — export again and re-plan";
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
