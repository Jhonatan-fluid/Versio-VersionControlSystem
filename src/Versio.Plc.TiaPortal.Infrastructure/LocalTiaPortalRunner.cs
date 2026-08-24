using System.Diagnostics;
using System.Text;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Platform;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>
/// Runs a Versio job inside the net48 Openness host.
/// </summary>
/// <remarks>
/// Openness assemblies are .NET Framework 4.8 and cannot load in Versio's net9 process.
/// The host is the analogue of CODESYS.exe --noUI --runscript: one child, one job file,
/// JSON-lines back. Siemens.Engineering.dll is never shipped; the host resolves it from
/// the install at runtime.
/// </remarks>
public sealed class LocalTiaPortalRunner(
    string hostExecutablePath,
    string profile,
    IVersioLogger? logger = null) : IPlcRunner
{
    private const string GlobalMutexName = @"Global\Versio.TiaPortal.SingleInstance";

    private static readonly TimeSpan MutexWait = TimeSpan.FromMinutes(15);

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    /// <summary>
    /// The host beside Versio.exe, beside the infrastructure output, or null.
    /// </summary>
    public static string? FindHost()
    {
        foreach (var candidate in HostCandidates(AppContext.BaseDirectory))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var asm = typeof(LocalTiaPortalRunner).Assembly.Location;
        if (!string.IsNullOrEmpty(asm))
        {
            var dir = Path.GetDirectoryName(asm);
            if (dir is not null)
            {
                var beside = Path.Combine(dir, "tia", "Versio.Plc.TiaPortal.Host.exe");
                if (File.Exists(beside))
                {
                    return beside;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Where the host is looked for, given the directory Versio.exe (or a test) is running from.
    /// </summary>
    /// <remarks>
    /// The pack layout is <c>tia\</c> beside the main exe. The second candidate is a flat
    /// copy, which is how a developer F5 output used to look before the targets file nested
    /// it. Tests pin this list so a packaging change that moves the host cannot pass as a
    /// FindHost that "still works" against a leftover file.
    /// </remarks>
    public static IReadOnlyList<string> HostCandidates(string baseDirectory) =>
    [
        Path.Combine(baseDirectory, "tia", "Versio.Plc.TiaPortal.Host.exe"),
        Path.Combine(baseDirectory, "Versio.Plc.TiaPortal.Host.exe"),
    ];

    public async Task<PlcResult> RunAsync(
        PlcJob job, IProgress<PlcProgress>? progress, CancellationToken cancellationToken)
    {
        using var scope = _log.BeginOperation(job.OperationId);

        if (job.Projects.Count != 1)
        {
            return PlcResult.Failure(
                $"a TIA Portal job carries exactly one project; this one carries {job.Projects.Count}.");
        }

        var only = job.Projects[0];
        var install = new TiaPortalLocator(logger: _log).FindInstalls()
            .FirstOrDefault(i => string.Equals(i.Version, profile, StringComparison.OrdinalIgnoreCase))
            ?? new TiaPortalLocator(logger: _log).FindInstalls().FirstOrDefault();

        if (install is null)
        {
            return PlcResult.Failure("TIA Portal is not installed on this machine.");
        }

        // The orchestrator leaves the plan null only when no format claims the path any
        // more, and "the runner decides" what that means. Fabricating an empty plan here
        // decided it was a successful no-op — an import that applies nothing, saves, and
        // reports green. Refusing with a sentence is the honest decision.
        var plan = only.Plan;
        if (job.Operation == PlcOperation.Import && plan is null)
        {
            return PlcResult.Failure(
                "an import without a plan is refused — nothing was planned, so nothing would "
                + "be applied.");
        }

        var native = new TiaPortalJob
        {
            Operation = job.Operation.ToString(),
            ProjectPath = only.ProjectPath,
            Path = only.Path,
            ApiFolder = install.ApiFolder,
            InstallFolder = install.InstallFolder,
            OperationId = job.OperationId,
            Save = job.Save,
            TimeoutSeconds = Math.Max((int)job.Timeout.TotalSeconds, 1200),
            Plan = plan,
        };

        var jobFile = WriteJobFile(native);
        _log.Info(TiaPortalLog.Category,
            $"{job.Operation} of {only.ProjectPath} via {hostExecutablePath} [{profile}]");
        _log.Debug(TiaPortalLog.Category, $"job file {jobFile}, timeout {native.TimeoutSeconds}s");

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
        TiaPortalJob job, string jobFile,
        IProgress<PlcProgress>? progress, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, GlobalMutexName);
        var acquired = false;
        var waited = Stopwatch.StartNew();
        try
        {
            try
            {
                acquired = ChildProcess.WaitMutex(mutex, MutexWait, cancellationToken);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                _log.Warn(TiaPortalLog.Category,
                    "recovered an abandoned TIA Portal lock — a previous Versio process died " +
                    "while holding it");
            }
            catch (OperationCanceledException)
            {
                _log.Warn(TiaPortalLog.Category,
                    "cancelled while waiting for the machine-wide TIA Portal lock");
                throw;
            }

            waited.Stop();
            if (!acquired)
            {
                throw new TimeoutException(
                    "another TIA Portal operation has been running for over 15 minutes");
            }

            if (waited.Elapsed > TimeSpan.FromSeconds(1))
            {
                _log.Info(TiaPortalLog.Category,
                    $"waited {waited.Elapsed.TotalSeconds:F1}s for the machine-wide TIA Portal lock");
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
        TiaPortalJob job, string jobFile,
        IProgress<PlcProgress>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = hostExecutablePath,
            Arguments = Quote(jobFile),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
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

            var parsed = TiaPortalProtocol.Parse(e.Data);
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

        _log.Debug(TiaPortalLog.Category, $"exec {hostExecutablePath} {psi.Arguments}");
        ChildProcess.StampParent(psi);
        // Portal is a COM server, not a child of Host.exe. Snapshot before start so a
        // later kill reaps only the instance this run created, not one the user had open.
        var portalBefore = ChildProcess.PidsNamed("Siemens.Automation.Portal");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = TimeSpan.FromSeconds(job.TimeoutSeconds);
        try
        {
            if (!ChildProcess.WaitForExitOrKill(
                    process, timeout, cancellationToken, closeStdinOnCancel: true))
            {
                LogReaped(ChildProcess.KillNew(portalBefore, "Siemens.Automation.Portal"),
                    "Siemens.Automation.Portal");
                _log.Error(TiaPortalLog.Category, $"timed out after {timeout} — killed the host");
                return PlcResult.Failure("the TIA Portal host did not finish within the timeout");
            }
        }
        catch (OperationCanceledException)
        {
            LogReaped(ChildProcess.KillNew(portalBefore, "Siemens.Automation.Portal"),
                "Siemens.Automation.Portal");
            _log.Warn(TiaPortalLog.Category,
                "cancelled — closed stdin so the host can Quit COM, then killed the host if it stayed up");
            throw;
        }

        string stdout;
        lock (gate)
        {
            stdout = buffer.ToString();
        }

        var output = TiaPortalProtocol.Parse(stdout);
        foreach (var line in output.Chatter)
        {
            _log.Debug(TiaPortalLog.Category, line);
        }

        // A malformed protocol line may be the fatal that explains everything else —
        // dropping it silently is how a failure arrives with no message at all.
        foreach (var line in output.Malformed)
        {
            _log.Warn(TiaPortalLog.Category, "malformed protocol line: " + line);
        }

        if (output.Fatal is { } fatal)
        {
            var msg = fatal.String("msg") ?? fatal.Raw;
            _log.Error(TiaPortalLog.Category, msg);
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
            _log.Error(TiaPortalLog.Category, $"host exited {process.ExitCode}");
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

    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;

    private void LogReaped(int count, string processName)
    {
        if (count <= 0)
        {
            return;
        }

        // KillTree on Host.exe does not reach the COM server. Zero is the usual
        // case: the host Quit and Portal went with it. A number here is the hole
        // that used to leave Siemens.Automation.Portal running after Stop.
        _log.Warn(TiaPortalLog.Category,
            $"reaped {count} leftover {processName} process(es) this run started — not ones already open");
    }

    private static string WriteJobFile(TiaPortalJob job)
    {
        var path = Path.Combine(Path.GetTempPath(), $"versio-tia-{job.OperationId}.json");
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
