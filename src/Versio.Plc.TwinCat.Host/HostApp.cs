using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// DTE session: probe, export, import, ioprobe. COM types are late-bound so this
/// project compiles without Beckhoff DLLs on the machine that builds Versio.
/// </summary>
internal static partial class HostApp
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static int Run(string json)
    {
        var job = JobReader.Parse(json);
        Emit("info", "op", job.Op, "project", job.Project ?? "", "progId", job.ProgId);

        object? dte = null;
        using var lifetime = HostLifetime.Attach(() => TryQuit(dte));
        try
        {
            if (string.IsNullOrWhiteSpace(job.Project) || !File.Exists(job.Project))
            {
                EmitFatal("project file is missing: " + job.Project);
                return 3;
            }

            var sln = SiblingSolution(job.Project!);
            if (sln is null)
            {
                EmitFatal(
                    "no sibling .sln beside the .tsproj; refusing to invent a solution in the user's tree");
                return 3;
            }

            if (!job.AllowConvert && ConvertRefusal(job.Project!, job.Version, job.Install) is { } refusal)
            {
                EmitFatal(refusal);
                return 7;
            }

            dte = CreateDte(job.ProgId);
            OpenSolution(dte, sln);

            var sysMan = SysManagerOf(dte);
            if (sysMan is null)
            {
                EmitFatal("the opened solution has no ITcSysManager — is this a TwinCAT project?");
                return 4;
            }

            var plcRoot = WaitForPlcProject(sysMan);
            if (plcRoot is null)
            {
                EmitFatal(
                    "PLC source tree did not load (TIPC is lazy). LookupTreeItem of the nested " +
                    "PLC project stayed empty.");
                return 4;
            }

            var op = job.Op.ToLowerInvariant();
            if (op == "probe")
            {
                Probe(sysMan, plcRoot, sln, job.Out);
            }
            else if (op == "export")
            {
                if (string.IsNullOrWhiteSpace(job.Out))
                {
                    EmitFatal("export job has no out directory");
                    return 5;
                }

                Export(sysMan, plcRoot, job.Out!);
            }
            else if (op == "import")
            {
                if (string.IsNullOrWhiteSpace(job.Src))
                {
                    EmitFatal("import job has no src directory");
                    return 5;
                }

                Import(dte, sysMan, plcRoot, job.Src!, job.Plan, job.Save);
            }
            else if (op == "ioprobe")
            {
                if (string.IsNullOrWhiteSpace(job.Out))
                {
                    EmitFatal("ioprobe job has no out directory");
                    return 5;
                }

                IoProbe(sysMan, job.Out!, job.WriteTest, job.CreateTest);
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
            TryQuit(dte);
        }
    }

    private static object CreateDte(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
                   ?? throw new InvalidOperationException(
                       progId + " is not registered. Install TwinCAT 3 XAE.");
        var created = Stopwatch.StartNew();

        // XAE is a COM server the activation launches, not a child of this process, so the
        // only way to know which TcXaeShell is ours is to look across the creation call
        // itself. Reported to the runner as ide_pid; on Stop it reaps that PID and nothing
        // else. Before this the runner diffed a snapshot taken over the whole run and
        // killed every XAE that appeared meanwhile, the engineer's included (the 2026-10-24
        // review's item 28). Ambiguous reports none: an XAE left running beats a stranger's
        // killed.
        var before = XaePids();
        var dte = Activator.CreateInstance(type)
                  ?? throw new InvalidOperationException("failed to construct " + progId);
        created.Stop();
        Emit("info", "phase", "dte", "detail", "created " + progId + " in " + created.ElapsedMilliseconds + "ms");
        var mine = XaePids().Except(before).ToList();
        if (mine.Count == 1)
        {
            Emit("ide_pid", "pid", mine[0].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            Emit("warning", "msg", "could not tell which XAE this host created (" +
                                   mine.Count + " appeared); Stop will not reap by name");
        }

        try
        {
            Com.Set(dte, "SuppressUI", true);
        }
        catch (Exception ex)
        {
            Emit("warning", "msg", "SuppressUI: " + Innermost(ex));
        }

        try
        {
            var window = Com.Get(dte, "MainWindow");
            if (window is not null)
            {
                Com.Set(window, "Visible", false);
            }
        }
        catch (Exception ex)
        {
            Emit("warning", "msg", "MainWindow.Visible: " + Innermost(ex));
        }

        return dte;
    }

    private static HashSet<int> XaePids()
    {
        try
        {
            return [.. Process.GetProcessesByName("TcXaeShell").Select(p => p.Id)];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private static void OpenSolution(object dte, string sln)
    {
        var solution = Com.Get(dte, "Solution")
                       ?? throw new InvalidOperationException("DTE.Solution missing");
        var opened = Stopwatch.StartNew();
        Com.Call(solution, "Open", sln);
        opened.Stop();
        Emit("info", "phase", "open", "detail", "Solution.Open in " + opened.ElapsedMilliseconds + "ms");
    }

    private static object? SysManagerOf(object dte)
    {
        var solution = Com.Get(dte, "Solution");
        var projects = Com.Get(solution!, "Projects");
        var count = Com.Int(projects, "Count");
        if (count < 1)
        {
            return null;
        }

        // EnvDTE Projects.Item is 1-based.
        var project = Com.Call(projects!, "Item", 1);
        return project is null ? null : Com.Get(project, "Object");
    }

    /// <summary>
    /// TIPC is lazy: the nested "&lt;name&gt; Project" node does not exist until the branch
    /// is touched. This loop touches and retries. What it must never do is return a node
    /// whose children have not loaded — an export walking one writes an empty mirror,
    /// reports success, and the resulting diff reads as every object deleted. The first
    /// cut ended its coalesce in "?? child", which made the retry loop dead code and did
    /// exactly that; it also fast-pathed the gate fixture by name, so the gate never saw
    /// the generic branch it was supposed to prove.
    /// </summary>
    private static object? WaitForPlcProject(object sysMan)
    {
        const int Tries = 90;

        // The first half of the wait belongs to the nested node exclusively; only after
        // that may an outer child with demonstrably loaded children stand in for it.
        const int Grace = 45;

        for (var i = 0; i < Tries; i++)
        {
            var tipc = Lookup(sysMan, "TIPC");
            if (tipc is not null)
            {
                Touch(tipc);
                var n = ChildCountOf(tipc);
                if (i == 0 || i % 10 == 0)
                {
                    Emit("info", "phase", "tipc", "detail", "TIPC ChildCount=" + n + " try=" + i);
                }

                for (var c = 1; c <= Math.Max(n, 1); c++)
                {
                    var child = ChildOf(tipc, c);
                    if (child is null)
                    {
                        continue;
                    }

                    Touch(child);
                    var name = Com.Str(child, "Name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var nestedPath = "TIPC^" + name + "^" + name + " Project";
                    var nested = Lookup(sysMan, nestedPath) ?? ChildNamed(child, name + " Project");
                    if (nested is not null)
                    {
                        Touch(nested);
                        Emit("info", "phase", "tipc", "detail",
                            "loaded " + nestedPath + " after " + (i * 500) + "ms");
                        return nested;
                    }

                    if (i >= Grace && ChildCountOf(child) > 0)
                    {
                        Emit("warning", "msg",
                            "no nested '" + name + " Project' node appeared; exporting from " +
                            "the loaded '" + name + "' node instead");
                        return child;
                    }
                }
            }

            Thread.Sleep(500);
        }

        return null;
    }

    private static void Touch(object item)
    {
        try
        {
            _ = Com.Str(item, "Name");
            _ = ChildCountOf(item);
            try
            {
                Com.Call(item, "ProduceXml");
            }
            catch
            {
                // Not every node produces XML; touching Name/ChildCount is the load trigger.
            }
        }
        catch
        {
            // Lazy nodes throw until they exist.
        }
    }

    private static int ChildCountOf(object item)
    {
        try
        {
            return Com.Int(item, "ChildCount");
        }
        catch
        {
            return 0;
        }
    }

    private static object? ChildOf(object parent, int index)
    {
        try
        {
            return Com.GetIndexed(parent, "Child", index) ?? Com.Call(parent, "Child", index);
        }
        catch
        {
            return null;
        }
    }

    private static object? ChildNamed(object parent, string name)
    {
        var n = ChildCountOf(parent);
        for (var i = 1; i <= n; i++)
        {
            var child = ChildOf(parent, i);
            if (child is not null
                && string.Equals(Com.Str(child, "Name"), name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    internal static object? Lookup(object sysMan, string path)
    {
        try
        {
            return Com.Call(sysMan, "LookupTreeItem", path);
        }
        catch
        {
            return null;
        }
    }

    private static void Probe(object sysMan, object plcRoot, string sln, string? outDir)
    {
        var occupancy = Path.ChangeExtension(sln, ".~u");
        var report = new Dictionary<string, object?>
        {
            ["plc"] = Com.Str(plcRoot, "Name"),
            ["pathName"] = Com.Str(plcRoot, "PathName"),
            ["childCount"] = Com.Int(plcRoot, "ChildCount"),
            ["occupancyMarker"] = occupancy,
            ["occupancyPresent"] = File.Exists(occupancy),
        };

        Emit("object", "path", Com.Str(plcRoot, "Name") ?? "PLC", "type", "PLC");
        if (Lookup(sysMan, "TIID") is not null)
        {
            Emit("object", "path", "TIID", "type", "TIID");
        }

        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outDir, "report.json"), json, Utf8NoBom);
        }
    }

    private static string? SiblingSolution(string projectPath)
    {
        var sln = Path.ChangeExtension(projectPath, ".sln");
        return File.Exists(sln) ? sln : null;
    }

    /// <summary>
    /// Compare the project's <c>TcVersion</c> to the install's build. A 4024 project
    /// opened on 4026 is the conversion we refuse — the same class of mistake as
    /// OpenWithUpgrade. Null means the open may proceed; a sentence is the refusal.
    /// </summary>
    /// <remarks>
    /// Unknown refuses. This used to return "no conversion" when either version could
    /// not be read — and missing information is exactly when the mistake happens: with
    /// SuppressUI set, a mismatched project converts headless with nobody asked. The
    /// mitigations for a false refusal live in version discovery (the job's version,
    /// Build_*, Version.txt, TWINCAT3DIR), not in skipping the check.
    /// </remarks>
    private static string? ConvertRefusal(string tsproj, string installVersion, string installFolder)
    {
        const string tail = "; refusing to open it so it is not silently converted. " +
                            "Convert a copy under %TEMP% if you mean to.";

        var project = ProjectTcVersion(tsproj);
        if (project is null)
        {
            return "the project's TcVersion could not be read from " +
                   Path.GetFileName(tsproj) + tail;
        }

        var install = string.IsNullOrWhiteSpace(installVersion) || installVersion == "unknown"
            ? InstallTcVersion(installFolder)
            : installVersion;
        if (install is null)
        {
            return "this project is TwinCAT " + project + " and the install's version is " +
                   "unknown — no Build_* folder, Version.txt or TWINCAT3DIR was found" + tail;
        }

        if (!string.Equals(MajorMinorBuild(project), MajorMinorBuild(install), StringComparison.Ordinal))
        {
            return "this project is TwinCAT " + project + " and the install is " + install + tail;
        }

        return null;
    }

    private static string? ProjectTcVersion(string tsproj)
    {
        try
        {
            var doc = XDocument.Load(tsproj);
            return doc.Root?.Attribute("TcVersion")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// TwinCAT's own build, not the Visual Studio shell's. <c>TcXaeShell.exe</c>
    /// ProductVersion is 15.0.0.0 on the 17.0 host — comparing that to a project's
    /// <c>TcVersion</c> refused every open as a conversion (Findings, 2026-08-19).
    /// Same walk as <c>TwinCatLocator.ReadTwinCatVersion</c>; duplicated because the
    /// host cannot reference Infrastructure.
    /// </summary>
    private static string? InstallTcVersion(string installFolder)
    {
        // Unknown now refuses the open, so a layout the locator's folder candidates do
        // not cover must still resolve. The Beckhoff installer sets TWINCAT3DIR
        // machine-wide; it is the fallback, never the override of what the job carries.
        var folder = !string.IsNullOrEmpty(installFolder) && Directory.Exists(installFolder)
            ? installFolder
            : TwinCatDirFromEnvironment();
        if (folder is null)
        {
            return null;
        }

        var plc = Path.Combine(folder, "Components", "Plc");
        if (Directory.Exists(plc))
        {
            var build = Directory.EnumerateDirectories(plc, "Build_*")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.StartsWith("Build_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(BuildOrdinal)
                .FirstOrDefault();
            if (build is not null)
            {
                return "3.1." + build["Build_".Length..];
            }
        }

        try
        {
            var versionTxt = Path.Combine(folder, "Version.txt");
            return File.Exists(versionTxt) ? File.ReadAllText(versionTxt).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? TwinCatDirFromEnvironment()
    {
        var dir = Environment.GetEnvironmentVariable("TWINCAT3DIR");
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
    }

    // "Build_4026.25" → (4026, 25). An ordinal string sort would put a five-digit build
    // below a four-digit one.
    private static (int Major, int Minor) BuildOrdinal(string name)
    {
        var parts = name["Build_".Length..].Split('.');
        return (int.TryParse(parts[0], out var major) ? major : 0,
                parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 0);
    }

    private static string MajorMinorBuild(string version)
    {
        // TwinCAT writes 3.1.4024.4; FileVersion may be 4026.0.40.0 or 3.1.4026.25.
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 && parts[0] == "3" && parts[1] == "1")
        {
            return "3.1." + parts[2];
        }

        // ProductVersion 4026.0.40.0 — take the first token as the build.
        if (parts.Length >= 1 && parts[0].Length == 4 && parts[0].StartsWith("40", StringComparison.Ordinal))
        {
            return "3.1." + parts[0];
        }

        return version;
    }

    private static void TryQuit(object? dte)
    {
        if (dte is null)
        {
            return;
        }

        try
        {
            var solution = Com.Get(dte, "Solution");
            if (solution is not null)
            {
                try
                {
                    Com.Call(solution, "Close", false);
                }
                catch
                {
                    // Closing after a failed open still has to return a fatal we already emitted.
                }
            }

            Com.Call(dte, "Quit");
        }
        catch
        {
            // Best effort. A leftover shell is a finding, not a second fatal.
        }
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        var license = ex.Message;
        if (license.Contains("license", StringComparison.OrdinalIgnoreCase))
        {
            return "TwinCAT XAE refused the session (license): " + license;
        }

        return license;
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

    // COM error messages carry tabs and stranger control characters; unescaped they make
    // the one protocol line that mattered malformed JSON, and the runner then reports the
    // generic "host exited 1" instead of the message.
    private static string Esc(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                sb.Append("\\\\");
            }
            else if (ch == '"')
            {
                sb.Append("\\\"");
            }
            else if (ch is '\r' or '\n')
            {
                sb.Append(' ');
            }
            else if (ch < ' ')
            {
                sb.Append("\\u").Append(((int)ch).ToString("x4"));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
