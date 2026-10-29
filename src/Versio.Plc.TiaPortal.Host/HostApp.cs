using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Openness session: probe, export, import. Types are loaded by name so this project
/// compiles without Siemens DLLs on the machine that builds Versio.
/// </summary>
/// <remarks>
/// Split along the existing methods — <c>HostExport</c>, <c>HostImport</c>,
/// <c>HostHardwareProbe</c>, <c>HostDeviceCatalog</c>, <c>HostReflection</c> —
/// so a 1,100-line file is not the unit of change. One type, still reflection-only,
/// still no Core reference.
/// </remarks>
internal static partial class HostApp
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
        using var lifetime = HostLifetime.Attach(() =>
        {
            TryDispose(project);
            TryDispose(portal);
        });
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

                Export(project, job.Out!, DescribeToolchain(job.Install));
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
            else if (op == "hwprobe")
            {
                if (string.IsNullOrWhiteSpace(job.Out))
                {
                    EmitFatal("hwprobe job has no out directory");
                    return 5;
                }

                HardwareProbe(project, job.Out!, job.WriteTest);
            }
            else if (op == "stampprobe")
            {
                if (string.IsNullOrWhiteSpace(job.Out))
                {
                    EmitFatal("stampprobe job has no out directory");
                    return 5;
                }

                StampProbe(project, job.Out!, job.Steps ?? [], job.Plc);
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

        // Portal is a COM server the activation launches, not a child of this process, so
        // the only way to know which Siemens.Automation.Portal is ours is to look across
        // the creation call itself. Reported to the runner as ide_pid; on Stop it reaps
        // that PID and nothing else. Before this the runner diffed a snapshot taken over
        // the whole run and killed every Portal that appeared meanwhile, the engineer's
        // included (the 2026-10-24 review's item 28). Ambiguous — more than one appeared
        // in the window — reports none: a Portal left running beats a stranger's killed.
        var before = PortalPids();
        var portal = Activator.CreateInstance(tiaType, withoutUi)
               ?? throw new InvalidOperationException("failed to construct TiaPortal");
        var mine = PortalPids().Except(before).ToList();
        if (mine.Count == 1)
        {
            Emit("ide_pid", "pid", mine[0].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            Emit("warning", "msg", "could not tell which Portal this host created (" +
                                   mine.Count + " appeared); Stop will not reap by name");
        }

        return portal;
    }

    private static HashSet<int> PortalPids()
    {
        try
        {
            return [.. System.Diagnostics.Process.GetProcessesByName("Siemens.Automation.Portal").Select(p => p.Id)];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
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
}
