using System.Text.Json;

namespace Versio.Plc.TwinCat.Host;

internal static partial class HostApp
{
    private static void Import(object dte, object sysMan, object plcRoot, string srcDir, JsonElement plan, bool save)
    {
        // Thrown, not EmitFatal-and-return: Run's success path emits done and exits 0,
        // so a bare return here would report a refused import as a green one.
        if (plan.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "import job has no plan — nothing was confirmed, so nothing is applied");
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

        var index = IndexTree(plcRoot);

        // Objects renamed this session, still reachable by the PathName the plan recorded.
        // A rename changes the live PathName immediately, so a later ApplyText planned
        // against the old identity would miss ByGuid and have nothing safe to fall back on.
        var renamed = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var rename in renames)
        {
            var guid = Str(rename, "guid");
            var newName = Str(rename, "newName");
            if (guid is null || newName is null)
            {
                throw new InvalidOperationException(
                    "a planned rename carries no identity or no new name — the plan is malformed");
            }

            if (!index.ByGuid.TryGetValue(guid, out var found))
            {
                // The tree no longer matches what the plan was made from. Renaming a
                // guessed object is how a different POU gets somebody else's name, so
                // the whole import stops here — nothing has been saved yet.
                throw new InvalidOperationException(
                    "rename target " + guid + " is not in the project — the tree changed " +
                    "since the export; export again and re-plan");
            }

            // Name only. A folder move is planned as IsMove; relocating under a new
            // parent is not separately gated — same first-loop bargain as TIA.
            Com.Set(found, "Name", newName);
            renamed[guid] = found;
            Emit("object", "path", newName, "action", "rename");
        }

        index = IndexTree(plcRoot);

        foreach (var file in files)
        {
            var action = Str(file, "action") ?? "";
            var rel = Str(file, "path") ?? "";
            var abs = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (action.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(abs))
            {
                // The plan was made from this very mirror. A planned file absent from disk
                // means the staging is wrong, and carrying on would silently import less
                // than the person confirmed. Nothing is saved on this path.
                throw new InvalidOperationException("planned file is missing on disk: " + rel);
            }

            if (action is "text" or "ApplyText")
            {
                if (rel.EndsWith(".devices.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    // Comment set and ProduceXml showed the marker in-session
                    // (ioprobe 2026-08-20). File.SaveAll persisted the companion
                    // ST edit and ITcSysManager.SaveProject is DISP_E_UNKNOWNNAME,
                    // so the next export still read an empty Comment. Applying
                    // here would confirm an import that silently reverts.
                    throw new InvalidOperationException(
                        rel + " is export-only — I/O Comment does not persist through " +
                        "Save on this Automation Interface");
                }

                ApplyText(index, renamed, file, abs);
                Emit("object", "path", rel, "action", action);
            }
            else if (action is "create" or "Create")
            {
                CreatePou(plcRoot, file, abs);
                Emit("object", "path", rel, "action", action);
                index = IndexTree(plcRoot);
            }
            else
            {
                // ApplyNative and anything a newer planner may emit. Dropping it on the
                // floor and then saving would report a partial import as a complete one.
                throw new InvalidOperationException(
                    "plan action '" + action + "' (" + rel + ") is not one this host " +
                    "applies — refusing to import less than was confirmed");
            }
        }

        foreach (var del in deletions)
        {
            var name = Str(del, "name");
            if (del.TryGetProperty("cannotVerify", out var cv)
                && cv.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(cv.GetString()))
            {
                Emit("warning", "msg", "kept " + name + ": " + cv.GetString());
                continue;
            }

            var guid = Str(del, "guid");
            if (guid is null)
            {
                Emit("warning", "msg",
                    "kept " + (name ?? "an object") + ": the plan carries no identity for it");
                continue;
            }

            // Identity only — never the name index. A same-named survivor (folders
            // included) would "match", and DeleteChild has nothing downstream to
            // object to a mistake. An object that moved in the IDE keeps living
            // under a new PathName; keeping it and saying so is the correct outcome.
            if (!index.ByGuid.TryGetValue(guid, out var found))
            {
                Emit("warning", "msg",
                    "kept " + (name ?? guid) + ": no live object at the recorded path — " +
                    "it may have been moved in the IDE; export again");
                continue;
            }

            // The contract on DeletedFile.Hash: the executing side compares the live
            // object before removing anything. A mismatch means it was edited in the IDE
            // since the export, and a missing mirror file is not permission to destroy
            // that edit.
            if (!LiveMatchesRecorded(found, del, out var why))
            {
                Emit("warning", "msg", "kept " + (name ?? guid) + ": " + why);
                continue;
            }

            try
            {
                // ITcSmTreeItem has DeleteChild on the parent, not Delete on the
                // item. item.Delete() is not on the interface — the 2026-08-22
                // Fact still re-exported the POU after a "successful" import
                // because the missing call was swallowed as a warning and the
                // object was kept.
                var parent = Com.Get(found, "Parent");
                if (parent is null)
                {
                    Emit("warning", "msg",
                        "kept " + (name ?? guid) + ": the live object has no parent to DeleteChild on");
                    continue;
                }

                Com.Call(parent, "DeleteChild", Com.Str(found, "Name") ?? name);
                Emit("object", "path", name ?? guid, "action", "delete");
            }
            catch (Exception ex)
            {
                Emit("warning", "msg", "delete failed: " + Innermost(ex));
            }
        }

        if (save)
        {
            SaveProject(dte, sysMan);
        }
    }

    private static bool LiveMatchesRecorded(object item, JsonElement del, out string why)
    {
        var recorded = new List<string>();
        if (del.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                foreach (var field in new[] { "hash", "normalizedHash" })
                {
                    var h = Str(f, field);
                    if (!string.IsNullOrEmpty(h))
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

        // The export hashed exactly the packed file it wrote — Join over the same two
        // texts this session reads back, so an untouched object reproduces its hash.
        var live = HashText(HostText.Join(
            TryText(item, "DeclarationText"), TryText(item, "ImplementationText")));
        if (recorded.Contains(live))
        {
            why = "";
            return true;
        }

        why = "the live object no longer matches the last export — it was edited in the " +
              "IDE since, and a missing mirror file is not permission to destroy that edit; " +
              "export again";
        return false;
    }

    private static void SaveProject(object dte, object sysMan)
    {
        try
        {
            Com.Call(dte, "ExecuteCommand", "File.SaveAll");
            return;
        }
        catch (Exception ex)
        {
            Emit("warning", "msg", "File.SaveAll: " + Innermost(ex));
        }

        try
        {
            Com.Call(sysMan, "SaveProject");
            return;
        }
        catch (Exception ex)
        {
            Emit("warning", "msg", "SaveProject: " + Innermost(ex));
        }

        var solution = Com.Get(dte, "Solution");
        var projects = Com.Get(solution!, "Projects");
        var count = Com.Int(projects, "Count");
        for (var i = 1; i <= count; i++)
        {
            var project = Com.Call(projects!, "Item", i);
            if (project is null)
            {
                continue;
            }

            try
            {
                Com.Call(project, "Save", "");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("project.Save failed: " + Innermost(ex));
            }
        }
    }

    private static void ApplyText(
        TreeIndex index, Dictionary<string, object> renamed, JsonElement file, string abs)
    {
        var guid = Str(file, "guid");
        var name = Str(file, "name");
        object? item = null;
        if (guid is not null && !index.ByGuid.TryGetValue(guid, out item))
        {
            // A rename this session moved the object's PathName; the plan still speaks
            // the old one. The rename loop kept the reference.
            renamed.TryGetValue(guid, out item);
        }

        if (item is null && name is not null)
        {
            // By display name only when exactly one object in the tree carries it. Names
            // recur — Init/Execute methods across FBs — and writing onto "the first one
            // found" lands the edit on the wrong POU. The read-back below cannot catch
            // that: it verifies the text we wrote is on the item we wrote to, not that
            // the item was the right one.
            if (index.AmbiguousNames.Contains(name))
            {
                throw new InvalidOperationException(
                    "several objects are named " + name + " and the recorded path " +
                    (guid ?? "(none)") + " matches none of them — refusing to guess " +
                    "which one this edit targets; export again and re-plan");
            }

            index.ByName.TryGetValue(name, out item);
        }

        if (item is null)
        {
            throw new InvalidOperationException("no live object for " + (guid ?? name ?? abs));
        }

        var packed = File.ReadAllText(abs);
        var (decl, impl) = HostText.Split(packed);
        SetTexts(item, decl, impl);

        var liveDecl = TryText(item, "DeclarationText") ?? "";
        if (!liveDecl.Contains(HostText.FirstNonComment(decl), StringComparison.Ordinal)
            && !string.IsNullOrEmpty(HostText.FirstNonComment(decl)))
        {
            throw new InvalidOperationException(
                "DeclarationText did not take the applied source for " + Path.GetFileName(abs));
        }

        // The implementation is checked as well as the declaration. It was not, and a
        // body deleted in the mirror stayed in the project while the import reported
        // success (the 2026-10-24 review's item 23): a cleared body must read back empty,
        // a written one must contain its first code line.
        if (!HostText.ImplementationTook(impl, TryText(item, "ImplementationText")))
        {
            throw new InvalidOperationException(
                (impl.Length == 0 ? "ImplementationText did not clear for "
                                  : "ImplementationText did not take the applied source for ")
                + Path.GetFileName(abs));
        }
    }

    private static void CreatePou(object plcRoot, JsonElement file, string abs)
    {
        var name = Str(file, "name") ?? Path.GetFileNameWithoutExtension(abs);
        var packed = File.ReadAllText(abs);
        var (decl, impl) = HostText.Split(packed);
        var subType = SubTypeOf(decl);
        var parent = ParentForCreate(plcRoot, file);

        object? created;
        try
        {
            // ITcSmTreeItem.CreateChild(name, nSubType, before, vInfo). vInfo for a
            // function block is IECLANGUAGETYPES — ST is 1. An empty string is not a
            // language int: XAE then throws "Must specify valid information for
            // parsing in the string." The I/O probe's guessed order (name, 1, "",
            // subType) treats 1 as nSubType — measured on TIID 2026-08-20, where the
            // child arrived as ItemSubType 1, not 111. Live XAE refused a PLC FB with
            // that order on 2026-08-22: "Couldn't create 'VersioGateFb_…'!".
            created = Com.Call(parent, "CreateChild", name, subType, "", IecLanguageSt);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("CreateChild failed for " + name + ": " + Innermost(ex));
        }

        if (created is null)
        {
            throw new InvalidOperationException("CreateChild returned nothing for " + name);
        }

        SetTexts(created, decl, impl);
    }

    /// <summary>
    /// <c>IECLANGUAGETYPES.IECLANGUAGE_ST</c> — Structured Text. Empty vInfo is not
    /// the documented default on this Automation Interface; it fails to parse.
    /// </summary>
    private const int IecLanguageSt = 1;

    private static void SetTexts(object item, string decl, string impl)
    {
        try
        {
            Com.Set(item, "DeclarationText", decl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("DeclarationText set failed: " + Innermost(ex));
        }

        // An empty implementation in the file is two different things: a DUT, which has
        // no body to write, or a POU whose body the engineer deleted. The first version
        // returned here for both, and the deleted body stayed in the project (the
        // 2026-10-24 review's item 23). HostText.ImplementationWriteFor tells them apart
        // by what the object holds now.
        var write = HostText.ImplementationWriteFor(impl, TryText(item, "ImplementationText"));
        if (write == HostText.ImplementationWrite.Leave)
        {
            return;
        }

        try
        {
            Com.Set(item, "ImplementationText", write == HostText.ImplementationWrite.Clear ? "" : impl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("ImplementationText set failed: " + Innermost(ex));
        }
    }

    private static int SubTypeOf(string declaration)
    {
        if (ContainsKeyword(declaration, "FUNCTION_BLOCK"))
        {
            return 604;
        }

        if (ContainsKeyword(declaration, "PROGRAM"))
        {
            return 602;
        }

        if (ContainsKeyword(declaration, "FUNCTION"))
        {
            return 603;
        }

        if (ContainsKeyword(declaration, "INTERFACE"))
        {
            return 618;
        }

        if (ContainsKeyword(declaration, "VAR_GLOBAL"))
        {
            return 615;
        }

        if (ContainsKeyword(declaration, "METHOD"))
        {
            return 609;
        }

        if (ContainsKeyword(declaration, "PROPERTY"))
        {
            return 611;
        }

        if (ContainsKeyword(declaration, "ACTION"))
        {
            return 608;
        }

        if (ContainsKeyword(declaration, "UNION"))
        {
            return 607;
        }

        if (ContainsKeyword(declaration, "STRUCT") || ContainsKeyword(declaration, "TYPE"))
        {
            return 606;
        }

        return 604;
    }

    private static object ParentForCreate(object plcRoot, JsonElement file)
    {
        var resolve = file.TryGetProperty("resolve", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
            : [];
        if (resolve.Count <= 1)
        {
            return plcRoot;
        }

        // Walk the recorded folder chain from the root rather than looking the parent's
        // bare name up in a tree-wide index — two folders named "Util" under different
        // branches would otherwise send the create to whichever the walk met first.
        var parent = plcRoot;
        foreach (var segment in resolve.Take(resolve.Count - 1))
        {
            var next = ChildNamed(parent, segment);
            if (next is null)
            {
                return plcRoot;
            }

            parent = next;
        }

        return parent;
    }


    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    private static TreeIndex IndexTree(object root)
    {
        var index = new TreeIndex();
        Collect(root, index);
        return index;
    }

    private static void Collect(object item, TreeIndex index)
    {
        var name = Com.Str(item, "Name");
        var path = Com.Str(item, "PathName");
        if (!string.IsNullOrEmpty(path))
        {
            index.ByGuid[path] = item;
        }

        if (!string.IsNullOrEmpty(name) && !index.ByName.TryAdd(name, item))
        {
            // A recurring name is remembered as such so nothing resolves through it —
            // "first one found" is not an identity.
            index.AmbiguousNames.Add(name);
        }

        var n = Com.Int(item, "ChildCount");
        for (var i = 1; i <= n; i++)
        {
            try
            {
                var child = ChildOf(item, i);
                if (child is not null)
                {
                    Collect(child, index);
                }
            }
            catch
            {
                // A child that is still lazy-loading is skipped; ApplyText then fails loudly.
            }
        }
    }

    private sealed class TreeIndex
    {
        public Dictionary<string, object> ByGuid { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
