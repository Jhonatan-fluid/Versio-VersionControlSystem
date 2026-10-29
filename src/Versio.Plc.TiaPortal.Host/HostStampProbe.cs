using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Tools-only measurement of the per-object modification stamp Openness exposes, so the
/// export can learn to skip what Portal says is unchanged. Not product Export.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a probe before a feature.</b> A full export of the owner's nine-PLC project costs
/// about 88 ms per object inside Portal (<c>Export</c> plus <c>GenerateSource</c>, 1,366
/// objects, 2026-10-29), on top of a fixed ~37 s to start Portal and open the project.
/// Skipping an unchanged object is the only lever on the per-object part, and the only
/// honest way to know an object is unchanged without exporting it is a stamp Portal
/// maintains itself. Whether <c>ModifiedDate</c> is that stamp — what moves it, what does
/// not, whether it survives save/close/reopen — is what this measures, one Portal session,
/// a scripted sequence of steps with a full dump of every stamp after each.
/// </para>
/// <para>
/// The steps are deliberately the operations a plant engineer performs: an SCL edit (through
/// the same <c>GenerateBlocksFromSource</c> door Versio's import uses), a UDT member added
/// (so the blocks and DBs that use it can be watched), a compile of the whole PLC, a second
/// compile with nothing edited, a rename, and a save. A second run of <c>dump</c> alone on
/// the saved project answers close-and-reopen.
/// </para>
/// </remarks>
internal static partial class HostApp
{
    private static readonly string[] StampAttributes =
    [
        "ModifiedDate", "CreationDate", "IsConsistent", "InterfaceModifiedDate",
        "StructureModified", "HeaderVersion", "AutoNumber",
    ];

    private static void StampProbe(object project, string outDir, IReadOnlyList<string> steps, string? plcName)
    {
        Directory.CreateDirectory(outDir);
        var plcs = WalkPlcSoftware(project).ToList();
        var plc = string.IsNullOrWhiteSpace(plcName)
            ? plcs.FirstOrDefault()
            : plcs.FirstOrDefault(p => string.Equals(p.DeviceName + "/" + p.PlcName, plcName, StringComparison.OrdinalIgnoreCase));
        if (plc is null)
        {
            throw new InvalidOperationException("stampprobe: no PLC named " + plcName);
        }

        var report = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["op"] = "stampprobe",
            ["projectName"] = Str(project, "Name") ?? "",
            ["plc"] = plc.DeviceName + "/" + plc.PlcName,
            ["steps"] = steps.ToList(),
        };
        var dumps = new List<Dictionary<string, object?>>();
        var log = new List<string>();
        report["dumps"] = dumps;
        report["log"] = log;

        foreach (var step in steps)
        {
            var colon = step.IndexOf(':');
            var verb = (colon < 0 ? step : step.Substring(0, colon)).Trim().ToLowerInvariant();
            var arg = colon < 0 ? "" : step.Substring(colon + 1);
            Emit("progress", "phase", "stampprobe", "detail", step);
            var sw = Stopwatch.StartNew();
            try
            {
                switch (verb)
                {
                    case "dump":
                        dumps.Add(DumpStamps(plcs, arg));
                        break;
                    case "inspect":
                        report["inspect"] = InspectStampSurface(plc);
                        break;
                    case "apply":
                        ApplyScl(plc, arg);
                        break;
                    case "compile":
                        CompileSoftware(plc, log);
                        break;
                    case "compileblock":
                        CompileOne(plc, arg, log);
                        break;
                    case "rename":
                        RenameOne(plc, arg, log);
                        break;
                    case "save":
                        Call(project, "Save");
                        break;
                    default:
                        log.Add("unknown step: " + step);
                        break;
                }

                log.Add(step + " ok in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                log.Add(step + " FAILED after " + sw.ElapsedMilliseconds + " ms: " + Innermost(ex));
                Emit("warning", "msg", step + ": " + Innermost(ex));
            }

            // Written after every step, so a step that takes Portal down still leaves the
            // dumps before it on disk.
            File.WriteAllText(Path.Combine(outDir, "stamps.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
        }

        Emit("info", "op", "stampprobe", "path", Path.Combine(outDir, "stamps.json"));
    }

    /// <summary>Every block, type and tag table of every PLC, with its stamp attributes.</summary>
    private static Dictionary<string, object?> DumpStamps(IReadOnlyList<PlcRef> plcs, string label)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var plc in plcs)
        {
            WalkForStamps(plc, GetProp(plc.Software, "BlockGroup"), "Blocks", "block", "", rows);
            WalkForStamps(plc, GetProp(plc.Software, "TypeGroup"), "Types", "type", "", rows);
            WalkForStamps(plc, GetProp(plc.Software, "TagTableGroup"), "TagTables", "table", "", rows);
        }

        return new Dictionary<string, object?>
        {
            ["label"] = label,
            ["at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["ms"] = sw.ElapsedMilliseconds,
            ["count"] = rows.Count,
            ["objects"] = rows,
        };
    }

    private static void WalkForStamps(
        PlcRef plc, object? group, string items, string kind, string chain,
        List<Dictionary<string, object?>> rows)
    {
        if (group is null)
        {
            return;
        }

        foreach (var item in Enumerate(GetProp(group, items)))
        {
            var name = Str(item, "Name") ?? "";
            var number = kind == "block" ? Str(item, "Number") ?? "0" : null;
            var typeName = kind == "block" ? BlockType(item) : kind == "type" ? "UDT" : "TAG_TABLE";
            var guid = number is null
                ? $"{plc.DeviceName}/{plc.PlcName}/{typeName}/{name}"
                : $"{plc.DeviceName}/{plc.PlcName}/{typeName}/{name}#{number}";
            var row = new Dictionary<string, object?>
            {
                ["guid"] = guid,
                ["kind"] = kind,
                ["group"] = chain,
                ["language"] = kind == "block" ? Str(item, "ProgrammingLanguage") ?? "" : "",
            };
            foreach (var attr in StampAttributes)
            {
                var value = TryAttribute(item, attr);
                if (value is not null)
                {
                    row[attr] = value;
                }
            }

            // The property, when Openness exposes one beside the attribute: the two must
            // agree or the probe would be measuring whichever one the export happens to read.
            try
            {
                var prop = GetProp(item, "ModifiedDate");
                if (prop is not null)
                {
                    row["ModifiedDateProperty"] = prop is DateTime dt
                        ? dt.ToString("o", CultureInfo.InvariantCulture)
                        : prop.ToString();
                }
            }
            catch
            {
                // Not every object has the property; the attribute is the measured surface.
            }

            rows.Add(row);
        }

        foreach (var child in Enumerate(GetProp(group, "Groups")))
        {
            var name = Str(child, "Name") ?? "Group";
            WalkForStamps(plc, child, items, kind, chain.Length == 0 ? name : chain + "/" + name, rows);
        }
    }

    /// <summary>What a block, a type, a tag table and the PLC software expose by name.</summary>
    private static Dictionary<string, object?> InspectStampSurface(PlcRef plc)
    {
        var result = new Dictionary<string, object?>();
        var block = Enumerate(GetProp(GetProp(plc.Software, "BlockGroup"), "Blocks")).FirstOrDefault();
        var type = Enumerate(GetProp(GetProp(plc.Software, "TypeGroup"), "Types")).FirstOrDefault();
        var table = Enumerate(GetProp(GetProp(plc.Software, "TagTableGroup"), "TagTables")).FirstOrDefault();
        foreach (var (label, obj) in new[] { ("block", block), ("type", type), ("table", table), ("software", (object?)plc.Software) })
        {
            if (obj is null)
            {
                continue;
            }

            result[label] = new Dictionary<string, object?>
            {
                ["type"] = obj.GetType().FullName,
                ["attributes"] = AttributeInfoNames(obj),
                ["properties"] = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name + ":" + p.PropertyType.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                ["methods"] = MethodNames(obj),
            };
        }

        return result;
    }

    private static List<string> AttributeInfoNames(object obj)
    {
        var names = new List<string>();
        try
        {
            var getInfos = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            foreach (var info in Enumerate(getInfos?.Invoke(obj, null)))
            {
                var name = TryStr(info, "Name") ?? info.ToString() ?? "";
                var mode = TryStr(info, "AccessMode") ?? "";
                names.Add(name + (mode.Length == 0 ? "" : " [" + mode + "]"));
            }
        }
        catch (Exception ex)
        {
            names.Add("GetAttributeInfos failed: " + Innermost(ex));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static void CompileSoftware(PlcRef plc, List<string> log)
    {
        var compilable = GetService(plc.Software, "Siemens.Engineering.Compiler.ICompilable")
                         ?? throw new InvalidOperationException("PlcSoftware has no ICompilable service");
        var compile = compilable.GetType().GetMethod("Compile", Type.EmptyTypes)
                      ?? compilable.GetType().GetInterfaces()
                          .Select(i => i.GetMethod("Compile", Type.EmptyTypes))
                          .FirstOrDefault(m => m is not null)
                      ?? throw new InvalidOperationException("ICompilable.Compile() missing");
        var result = compile.Invoke(compilable, null);
        log.Add("compile: state=" + (result is null ? "" : Str(result, "State"))
                + " errors=" + (result is null ? "" : Str(result, "ErrorCount"))
                + " warnings=" + (result is null ? "" : Str(result, "WarningCount")));
    }

    private static void CompileOne(PlcRef plc, string name, List<string> log)
    {
        var hit = FindSoftware(plc, name, isType: false)
                  ?? throw new InvalidOperationException("no block named " + name);
        TryCompile(hit.Obj);
        log.Add("compiled block " + name);
    }

    /// <summary><c>rename:block|type:Old:New</c>.</summary>
    private static void RenameOne(PlcRef plc, string arg, List<string> log)
    {
        var parts = arg.Split(':');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("rename wants kind:old:new, got " + arg);
        }

        var isType = string.Equals(parts[0], "type", StringComparison.OrdinalIgnoreCase);
        var hit = FindSoftware(plc, parts[1], isType)
                  ?? throw new InvalidOperationException("no " + parts[0] + " named " + parts[1]);
        SetProp(hit.Obj, "Name", parts[2]);
        log.Add("renamed " + parts[1] + " to " + (Str(hit.Obj, "Name") ?? "?"));
    }
}
