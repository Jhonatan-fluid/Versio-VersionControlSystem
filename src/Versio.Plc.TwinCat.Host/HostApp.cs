using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// DTE session: probe, export, import. COM types are late-bound so this project
/// compiles without Beckhoff DLLs on the machine that builds Versio.
/// </summary>
internal static partial class HostApp
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static int Run(string json)
    {
        var job = JobReader.Parse(json);
        Emit("info", "op", job.Op, "project", job.Project ?? "", "progId", job.ProgId);

        object? dte = null;
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

            if (!job.AllowConvert && WouldConvert(job.Project!, job.Version, job.Install))
            {
                EmitFatal(
                    "this project is TwinCAT " + ProjectTcVersion(job.Project!) +
                    " and the install is " + (string.IsNullOrEmpty(job.Version) ? "unknown" : job.Version) +
                    "; refusing to open it so it is not silently converted. Convert a copy if you mean to.");
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
        var dte = Activator.CreateInstance(type)
                  ?? throw new InvalidOperationException("failed to construct " + progId);
        created.Stop();
        Emit("info", "phase", "dte", "detail", "created " + progId + " in " + created.ElapsedMilliseconds + "ms");

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

    private static object? WaitForPlcProject(object sysMan)
    {
        var known = new[]
        {
            "TIPC^LibraryDocumentationSample^LibraryDocumentationSample Project",
            "TIPC^LibraryDocumentationSample",
            "TIPC",
        };

        for (var i = 0; i < 90; i++)
        {
            foreach (var path in known)
            {
                var hit = Lookup(sysMan, path);
                if (hit is not null && LooksLikePlcProject(hit, path))
                {
                    Emit("info", "phase", "tipc", "detail",
                        "loaded " + path + " after " + (i * 500) + "ms");
                    return NestedProjectOf(sysMan, hit, path);
                }
            }

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
                    var nested = Lookup(sysMan, nestedPath) ?? ChildNamed(child, name + " Project") ?? child;
                    if (nested is not null)
                    {
                        Touch(nested);
                        Emit("info", "phase", "tipc", "detail",
                            "loaded " + nestedPath + " after " + (i * 500) + "ms");
                        return nested;
                    }
                }
            }

            Thread.Sleep(500);
        }

        return null;
    }

    private static bool LooksLikePlcProject(object item, string path) =>
        path.EndsWith(" Project", StringComparison.Ordinal)
        || ChildCountOf(item) > 0
        || TryText(item, "DeclarationText") is not null;

    private static object NestedProjectOf(object sysMan, object item, string path)
    {
        if (path.EndsWith(" Project", StringComparison.Ordinal))
        {
            return item;
        }

        var name = Com.Str(item, "Name");
        if (string.IsNullOrEmpty(name))
        {
            return item;
        }

        return Lookup(sysMan, "TIPC^" + name + "^" + name + " Project")
               ?? ChildNamed(item, name + " Project")
               ?? item;
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
    /// Compare the project's <c>TcVersion</c> to the install folder name. A 4024 project
    /// opened on 4026 is the conversion we refuse — the same class of mistake as
    /// OpenWithUpgrade.
    /// </summary>
    private static bool WouldConvert(string tsproj, string installVersion, string installFolder)
    {
        var project = ProjectTcVersion(tsproj);
        var install = string.IsNullOrWhiteSpace(installVersion) || installVersion == "unknown"
            ? InstallTcVersion(installFolder)
            : installVersion;
        if (project is null || install is null)
        {
            return false;
        }

        return !string.Equals(MajorMinorBuild(project), MajorMinorBuild(install), StringComparison.Ordinal);
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
        if (string.IsNullOrEmpty(installFolder) || !Directory.Exists(installFolder))
        {
            return null;
        }

        var plc = Path.Combine(installFolder, "Components", "Plc");
        if (Directory.Exists(plc))
        {
            var build = Directory.EnumerateDirectories(plc, "Build_*")
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (build is not null && build.StartsWith("Build_", StringComparison.OrdinalIgnoreCase))
            {
                return "3.1." + build["Build_".Length..];
            }
        }

        var versionTxt = Path.Combine(installFolder, "Version.txt");
        return File.Exists(versionTxt) ? File.ReadAllText(versionTxt).Trim() : null;
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

    private static string Esc(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
