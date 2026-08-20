using System.Diagnostics;
using System.Text;
using Versio.Core.Logging;
using Versio.Core.Plc;
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
        var names = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tia", "Versio.Plc.TiaPortal.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "Versio.Plc.TiaPortal.Host.exe"),
        };

        foreach (var candidate in names)
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
                acquired = mutex.WaitOne(MutexWait);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                _log.Warn(TiaPortalLog.Category,
                    "recovered an abandoned TIA Portal lock — a previous Versio process died " +
                    "while holding it");
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

            _log.Error(TiaPortalLog.Category, $"timed out after {timeout} — killed the host");
            return PlcResult.Failure("the TIA Portal host did not finish within the timeout");
        }

        // WaitForExit(int) can return before the async readers have delivered their last
        // lines; only the parameterless overload is documented to wait for them. Without
        // it the final done line is sometimes still in flight when the buffer is read,
        // and a fully successful import reports "host exited 0" as a failure.
        process.WaitForExit();

        cancellationToken.ThrowIfCancellationRequested();

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
