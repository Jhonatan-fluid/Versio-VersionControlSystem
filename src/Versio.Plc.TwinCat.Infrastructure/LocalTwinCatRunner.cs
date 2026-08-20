using System.Diagnostics;
using System.Text;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Plc.TwinCat;

namespace Versio.Plc.TwinCat.Infrastructure;

/// <summary>
/// Runs a Versio job inside the TwinCAT helper host.
/// </summary>
/// <remarks>
/// DTE, STA and IOleMessageFilter must not sit on the WPF UI thread. The host is the
/// analogue of the TIA net48 helper: one child, one job file, JSON-lines back. Beckhoff
/// assemblies are never shipped; the host binds <c>TcXaeShell.DTE.17.0</c> from the
/// machine at runtime.
/// </remarks>
public sealed class LocalTwinCatRunner(
    string hostExecutablePath,
    string profile,
    IVersioLogger? logger = null) : IPlcRunner
{
    private const string GlobalMutexName = @"Global\Versio.TwinCat.SingleInstance";

    private static readonly TimeSpan MutexWait = TimeSpan.FromMinutes(15);

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    /// <summary>
    /// The host beside Versio.exe, beside the infrastructure output, or null.
    /// </summary>
    public static string? FindHost()
    {
        var names = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "twincat", "Versio.Plc.TwinCat.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "Versio.Plc.TwinCat.Host.exe"),
        };

        foreach (var candidate in names)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var asm = typeof(LocalTwinCatRunner).Assembly.Location;
        if (!string.IsNullOrEmpty(asm))
        {
            var dir = Path.GetDirectoryName(asm);
            if (dir is not null)
            {
                var beside = Path.Combine(dir, "twincat", "Versio.Plc.TwinCat.Host.exe");
                if (File.Exists(beside))
                {
                    return beside;
                }
            }
        }

        return null;
    }

    public async Task<PlcResult> RunAsync(
        PlcJob job, IProgress<PlcProgress>? progress, CancellationToken cancellationToken)
    {
        using var scope = _log.BeginOperation(job.OperationId);

        if (job.Projects.Count != 1)
        {
            return PlcResult.Failure(
                $"a TwinCAT job carries exactly one project; this one carries {job.Projects.Count}.");
        }

        var only = job.Projects[0];
        var install = new TwinCatLocator(_log).FindPreferred();
        if (install is null)
        {
            return PlcResult.Failure("TwinCAT XAE is not installed on this machine.");
        }

        var plan = only.Plan;
        if (job.Operation == PlcOperation.Import && plan is null)
        {
            plan = TwinCatProjectFormat.Instance.PlanImport(null, [], new Dictionary<string, string>());
        }

        var native = new TwinCatJob
        {
            Operation = job.Operation.ToString(),
            ProjectPath = only.ProjectPath,
            Path = only.Path,
            ProgId = string.IsNullOrEmpty(profile) ? install.ProgId : profile,
            InstallFolder = install.InstallFolder,
            Version = install.Version,
            OperationId = job.OperationId,
            Save = job.Save,
            TimeoutSeconds = Math.Max((int)job.Timeout.TotalSeconds, 1200),
            Plan = plan,
            // A %TEMP% copy is the conscious conversion the product allows. Opening the
            // path the user bound, on a newer install, is the OpenWithUpgrade class of
            // mistake and stays refused.
            AllowConvert = IsDisposableCopy(only.ProjectPath),
        };

        var jobFile = WriteJobFile(native);
        _log.Info(TwinCatLog.Category,
            $"{job.Operation} of {only.ProjectPath} via {hostExecutablePath} [{native.ProgId}]");
        _log.Debug(TwinCatLog.Category, $"job file {jobFile}, timeout {native.TimeoutSeconds}s");

        try
        {
            return await Task.Factory.StartNew(
                () => RunGuarded(native, jobFile, progress, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(jobFile);
        }
    }

    private PlcResult RunGuarded(
        TwinCatJob job, string jobFile,
        IProgress<PlcProgress>? progress, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, GlobalMutexName);
        var acquired = false;
        var waited = Stopwatch.StartNew();
        try
        {
            try
            {
                acquired = mutex.WaitOne(MutexWait);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                _log.Warn(TwinCatLog.Category,
                    "recovered an abandoned TwinCAT lock — a previous Versio process died " +
                    "while holding it");
            }

            waited.Stop();
            if (!acquired)
            {
                throw new TimeoutException(
                    "another TwinCAT operation has been running for over 15 minutes");
            }

            if (waited.Elapsed > TimeSpan.FromSeconds(1))
            {
                _log.Info(TwinCatLog.Category,
                    $"waited {waited.Elapsed.TotalSeconds:F1}s for the machine-wide TwinCAT lock");
            }

            return Execute(job, jobFile, progress, cancellationToken);
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private PlcResult Execute(
        TwinCatJob job, string jobFile,
        IProgress<PlcProgress>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = hostExecutablePath,
            Arguments = Quote(jobFile),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        var buffer = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var gate = new object();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (gate)
            {
                buffer.AppendLine(e.Data);
            }

            var parsed = TwinCatProtocol.Parse(e.Data);
            var evt = parsed.Events.FirstOrDefault();
            if (evt is not null && evt.Type == "progress")
            {
                progress?.Report(new PlcProgress(
                    job.OperationId, evt.String("phase") ?? "running",
                    null, null, evt.String("detail"), stopwatch.Elapsed));
            }
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;

        _log.Debug(TwinCatLog.Category, $"exec {hostExecutablePath} {psi.Arguments}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = TimeSpan.FromSeconds(job.TimeoutSeconds);
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            _log.Error(TwinCatLog.Category, $"timed out after {timeout} — killed the host");
            return PlcResult.Failure("the TwinCAT host did not finish within the timeout");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string stdout;
        lock (gate)
        {
            stdout = buffer.ToString();
        }

        var output = TwinCatProtocol.Parse(stdout);
        foreach (var line in output.Chatter)
        {
            _log.Debug(TwinCatLog.Category, line);
        }

        if (output.Fatal is { } fatal)
        {
            var msg = fatal.String("msg") ?? fatal.Raw;
            _log.Error(TwinCatLog.Category, msg);
            return new PlcResult
            {
                Succeeded = false,
                FailureReason = msg,
                Duration = stopwatch.Elapsed,
            };
        }

        var paths = output.Events
            .Where(e => e.Type == "object")
            .Select(e => e.String("path"))
            .OfType<string>()
            .ToList();

        var warnings = output.Events
            .Where(e => e.Type == "warning")
            .Select(e => e.String("msg") ?? e.String("detail") ?? e.Raw)
            .ToList();

        var ok = output.Completed && process.ExitCode == 0;
        if (!ok && process.ExitCode != 0)
        {
            _log.Error(TwinCatLog.Category, $"host exited {process.ExitCode}");
        }

        return new PlcResult
        {
            Succeeded = ok,
            FailureReason = ok ? null : (output.Fatal?.String("msg") ?? $"host exited {process.ExitCode}"),
            Duration = stopwatch.Elapsed,
            ChangedPaths = paths,
            Warnings = warnings,
        };
    }

    private static bool IsDisposableCopy(string projectPath)
    {
        var temp = Path.GetTempPath();
        return projectPath.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;

    private static string WriteJobFile(TwinCatJob job)
    {
        var path = Path.Combine(Path.GetTempPath(), $"versio-tc-{job.OperationId}.json");
        File.WriteAllText(path, job.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Temp. The next run uses a different id.
        }
    }
}
