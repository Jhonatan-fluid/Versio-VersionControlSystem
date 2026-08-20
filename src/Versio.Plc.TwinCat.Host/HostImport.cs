using System.Text.Json;

namespace Versio.Plc.TwinCat.Host;

internal static partial class HostApp
{
    private static void Import(object dte, object sysMan, object plcRoot, string srcDir, JsonElement plan, bool save)
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

        var index = IndexTree(plcRoot);

        foreach (var rename in renames)
        {
            var guid = Str(rename, "guid");
            var newName = Str(rename, "newName");
            if (guid is null || newName is null)
            {
                continue;
            }

            // Name only. A folder move is planned as IsMove; relocating under a new
            // parent is not separately gated — same first-loop bargain as TIA.
            if (index.ByGuid.TryGetValue(guid, out var found))
            {
                Com.Set(found, "Name", newName);
                Emit("object", "path", newName, "action", "rename");
            }
        }

        index = IndexTree(plcRoot);

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

            if (action is "text" or "ApplyText")
            {
                ApplyText(index, file, abs);
                Emit("object", "path", rel, "action", action);
            }
            else if (action is "create" or "Create")
            {
                CreatePou(plcRoot, index, file, abs);
                Emit("object", "path", rel, "action", action);
                index = IndexTree(plcRoot);
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

            if (index.ByGuid.TryGetValue(guid, out var found)
                || (Str(del, "name") is { } n && index.ByName.TryGetValue(n, out found)))
            {
                try
                {
                    Com.Call(found, "Delete");
                    Emit("object", "path", Str(del, "name") ?? guid, "action", "delete");
                }
                catch (Exception ex)
                {
                    Emit("warning", "msg", "delete failed: " + Innermost(ex));
                }
            }
        }

        if (save)
        {
            SaveProject(dte, sysMan);
        }
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

    private static void ApplyText(TreeIndex index, JsonElement file, string abs)
    {
        var guid = Str(file, "guid");
        var name = Str(file, "name");
        object? item = null;
        if (guid is not null)
        {
            index.ByGuid.TryGetValue(guid, out item);
        }

        if (item is null && name is not null)
        {
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
        if (!liveDecl.Contains(FirstNonComment(decl), StringComparison.Ordinal)
            && !string.IsNullOrEmpty(FirstNonComment(decl)))
        {
            throw new InvalidOperationException(
                "DeclarationText did not take the applied source for " + Path.GetFileName(abs));
        }
    }

    private static void CreatePou(object plcRoot, TreeIndex index, JsonElement file, string abs)
    {
        var name = Str(file, "name") ?? Path.GetFileNameWithoutExtension(abs);
        var packed = File.ReadAllText(abs);
        var (decl, impl) = HostText.Split(packed);
        var subType = SubTypeOf(decl);
        var parent = ParentForCreate(plcRoot, index, file) ?? plcRoot;

        object? created;
        try
        {
            // TREEITEMINSERT last = 1. Fourth argument is the IEC subtype.
            created = Com.Call(parent, "CreateChild", name, 1, "", subType);
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

        if (string.IsNullOrEmpty(impl))
        {
            return;
        }

        try
        {
            Com.Set(item, "ImplementationText", impl);
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

    private static object? ParentForCreate(object plcRoot, TreeIndex index, JsonElement file)
    {
        var resolve = file.TryGetProperty("resolve", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
            : [];
        if (resolve.Count <= 1)
        {
            return plcRoot;
        }

        var parentName = resolve[^2];
        return index.ByName.TryGetValue(parentName, out var parent) ? parent : plcRoot;
    }

    private static string FirstNonComment(string text)
    {
        foreach (var raw in text.Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            return line.Length > 40 ? line[..40] : line;
        }

        return "";
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

        if (!string.IsNullOrEmpty(name) && !index.ByName.ContainsKey(name))
        {
            index.ByName[name] = item;
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
    }
}
