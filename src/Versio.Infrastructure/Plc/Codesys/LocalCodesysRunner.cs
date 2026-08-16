using System.Diagnostics;
using System.Text;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;

namespace Versio.Infrastructure.Plc.Codesys;

/// <summary>
/// Runs a Versio script inside a headless CODESYS process.
/// </summary>
/// <remarks>
/// <para>
/// Everything here exists because of a measured failure mode:
/// </para>
/// <list type="bullet">
///   <item><b>Async pipe draining.</b> Reading a pipe to completion before
///     <c>WaitForExit</c> deadlocks once output exceeds the buffer, and a 2.5 MB
///     visualisation export exceeds it comfortably.</item>
///   <item><b>Job file, not command-line arguments.</b> Windows
///     <c>--scriptargs</c> quoting is genuinely awful; only one file path crosses the
///     boundary. The file is UTF-8 <b>without BOM</b>, because IronPython 2.7's
///     <c>json.load</c> fails on a BOM with "No JSON object could be decoded" — measured.</item>
///   <item><b>Process-tree kill on timeout.</b> A <c>--noUI</c> CODESYS sitting on a hidden
///     modal never exits, and it spawns children that outlive a plain
///     <c>Process.Kill()</c>.</item>
///   <item><b>Machine-wide mutex.</b> CODESYS is a desktop IDE, not a server; two
///     concurrent headless instances on one profile are not safe. This serialises across
///     every Versio process on the machine, not just within one.</item>
/// </list>
/// </remarks>
public sealed class LocalCodesysRunner(
    string codesysExecutablePath,
    ScriptExtractor scripts,
    IVersioLogger? logger = null) : IPlcRunner
{
    /// <summary>Global so two Versio windows cannot launch competing CODESYS instances.</summary>
    private const string GlobalMutexName = @"Global\Versio.Codesys.SingleInstance";

    private static readonly TimeSpan MutexWait = TimeSpan.FromMinutes(15);

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    /// <summary>
    /// Run a vendor-neutral job by translating it into CODESYS's own terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method is the vendor boundary.</b> Above it the sync engine speaks
    /// <see cref="PlcJob"/> and knows nothing about profiles, script protocols or exit codes;
    /// below it everything is CODESYS-specific. A Rockwell or TwinCAT runner implements the
    /// same interface and translates into whatever its tool wants, and nothing in
    /// <c>Sync/</c> changes.
    /// </para>
    /// <para>
    /// The translation is where the JSON-lines protocol stops. Object events become plain
    /// paths and warning events become sentences, so the orchestrator never learns what a
    /// CODESYS warning code looks like.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The translation itself, as a function — neutral job in, CODESYS job out.
    /// </summary>
    /// <remarks>
    /// <b>Public and static so it can be asserted without launching CODESYS.</b> It was inline
    /// in <see cref="RunAsync(PlcJob, IProgress{PlcProgress}, CancellationToken)"/>, which
    /// meant the only way to check that a field survives the boundary was a 17–31 s run — so
    /// in practice nothing checked. That mattered the moment the job grew a field whose loss
    /// is silent: drop <c>Plan</c> here and every import still works, still runs from a plan,
    /// and runs from a <i>different</i> one than the person was shown.
    /// </remarks>
    public static CodesysJob ToNative(PlcJob job) => new()
    {
        Operation = job.Operation switch
        {
            PlcOperation.Export => CodesysOperation.Export,
            PlcOperation.Import => CodesysOperation.Import,
            _ => CodesysOperation.Probe,
        },
        Profile = job.Profile,
        Projects = [.. job.Projects.Select(p => new ProjectJob
        {
            BindingId = p.BindingId,
            ProjectPath = p.ProjectPath,
            Path = p.Path,

            // Carried across, never recomputed. See the remarks above for why losing this
            // line is the kind of defect that leaves nothing on fire to point at.
            Plan = p.Plan,
        })],
        OperationId = job.OperationId,
        Save = job.Save,
        Timeout = job.Timeout,
    };

    public async Task<PlcResult> RunAsync(
        PlcJob job, IProgress<PlcProgress>? progress, CancellationToken cancellationToken)
    {
        var native = ToNative(job);

        // Progress crosses the boundary too, so the heartbeat above never sees a Codesys type.
        var relay = progress is null
            ? null
            : new Progress<CodesysProgress>(p => progress.Report(
                new PlcProgress(p.OperationId, p.Phase, p.Done, p.Total, p.Detail, p.Elapsed)));

        var result = await RunAsync(native, relay, cancellationToken).ConfigureAwait(false);

        return new PlcResult
        {
            Succeeded = result.Succeeded,
            FailureReason = result.FailureReason,
            Duration = result.Duration,
            ChangedPaths =
            [
                .. result.Output.Events
                    .Where(e => e.Type == "object")
                    .Select(e => e.String("path"))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => p!),
            ],
            Warnings =
            [
                .. result.Output.Warnings.Select(w => $"{w.String("code")}: {w.String("path")}"),
            ],
        };
    }

    public async Task<CodesysResult> RunAsync(
        CodesysJob job,
        IProgress<CodesysProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The job carries its own id, and this scope is what correlates the C# side, the
        // subprocess chatter and the heartbeat into one filterable stream. The scope does all
        // the work: the id travels into the job file as "operationId" and no script reads it
        // back or stamps it on a protocol line, so every event is attributed by virtue of
        // being parsed inside this scope rather than by carrying the id itself.
        using var scope = _log.BeginOperation(job.OperationId);

        // An import runs from a plan, decided before CODESYS starts — the script executes
        // exactly what the plan says and derives nothing from file names (Phase 1 of
        // docs/CODESYS-IMPORT-PLAN.md). A caller that computed its own plan keeps it; one
        // that did not gets it computed here, so every import is plan-driven whichever door
        // it came through, including a test driving the runner directly.
        if (job.Operation == CodesysOperation.Import
            && job.Projects.Count == 1 && job.Projects[0].Plan is null)
        {
            job = job with
            {
                Projects =
                [
                    job.Projects[0] with
                    {
                        Plan = await PlanImportAsync(job.Projects[0].Path, cancellationToken)
                            .ConfigureAwait(false),
                    },
                ],
            };
        }

        var script = ScriptFor(job.Operation);
        var scriptPath = scripts.PathFor(script);
        var jobFile = WriteJobFile(job);

        _log.Info(LogCategory.Codesys,
            $"{job.Operation} of {job.Projects.Count} project(s) using {script}" +
            (string.IsNullOrWhiteSpace(job.Profile) ? string.Empty : $" [{job.Profile}]"));

        foreach (var project in job.Projects)
        {
            _log.Debug(LogCategory.Codesys,
                $"{project.BindingId}: {project.ProjectPath} ↔ {project.Path}");
        }

        _log.Debug(LogCategory.Codesys, $"job file {jobFile}, timeout {job.Timeout}");

        try
        {
            // A named Mutex has THREAD AFFINITY: it must be released by the thread that
            // acquired it. Acquiring inside Task.Run and releasing in a finally after an
            // await lands on a different pool thread and throws
            //   ApplicationException: Object synchronization method was called from an
            //   unsynchronized block of code.
            //
            // So the whole guarded section runs on one dedicated long-running thread.
            // A named Semaphore would avoid the affinity problem but gives up abandonment
            // recovery - if a process died holding it the count would never come back, and
            // every future export on the machine would block forever. Mutex reports
            // AbandonedMutexException instead, which is exactly the case we must survive.
            return await Task.Factory.StartNew(
                () => RunGuarded(job, scriptPath, jobFile, progress, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(jobFile);
        }
    }

    /// <summary>Synchronous on purpose: it runs on its own long-running thread so the
    /// mutex is acquired and released by the same thread.</summary>
    private CodesysResult RunGuarded(
        CodesysJob job, string scriptPath, string jobFile,
        IProgress<CodesysProgress>? progress, CancellationToken cancellationToken)
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
                // A previous Versio died holding it. We are now the owner, and that is
                // safe: CODESYS state lives in files, not in the mutex.
                acquired = true;
                _log.Warn(LogCategory.Codesys,
                    "recovered an abandoned CODESYS lock — a previous Versio process died " +
                    "while holding it");
            }

            waited.Stop();

            if (!acquired)
            {
                _log.Error(LogCategory.Codesys,
                    $"gave up waiting for the machine-wide CODESYS lock after " +
                    $"{waited.Elapsed.TotalMinutes:F0} minutes");
                throw new TimeoutException(
                    "another CODESYS operation has been running for over 15 minutes");
            }

            // Only when it actually blocked. CODESYS is serialised machine-wide, so a wait
            // here means another Versio — possibly in another window, possibly another user
            // session — is mid-export, and that is the explanation for an export that
            // appeared to hang before it had even started.
            if (waited.Elapsed > TimeSpan.FromSeconds(1))
            {
                _log.Info(LogCategory.Codesys,
                    $"waited {waited.Elapsed.TotalSeconds:F1}s for the machine-wide CODESYS lock");
            }

            // Blocking on this dedicated thread is fine and keeps acquire and release
            // together: the work is a long external process, not CPU-bound.
            return ExecuteAsync(job, scriptPath, jobFile, progress, cancellationToken)
                .GetAwaiter().GetResult();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private async Task<CodesysResult> ExecuteAsync(
        CodesysJob job, string scriptPath, string jobFile,
        IProgress<CodesysProgress>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = codesysExecutablePath,
            // NOTE: Arguments, not ArgumentList.
            //
            // ArgumentList applies .NET's standard quoting, which wraps the WHOLE argument
            // when it contains a space: "--profile=CODESYS V3.5 SP22 Patch 2". CODESYS does
            // not parse that - it exits 1 in under a second having done nothing. It expects
            // the quotes around the VALUE only: --profile="CODESYS V3.5 SP22 Patch 2".
            //
            // Building the string by hand is the reason the job payload is a file: exactly
            // one path crosses this boundary, and none of these values can contain a quote.
            Arguments = BuildArguments(job.Profile, scriptPath, jobFile),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // CreateNoWindow alone is not enough here. It sets CREATE_NO_WINDOW, which
            // suppresses the console a CONSOLE subsystem child would allocate - and
            // CODESYS.exe is a GUI application. Under --noUI it still creates a top-level
            // window, and the engineer sees a CODESYS shell flash up and sit there for the
            // twenty seconds of an export they never asked to watch.
            //
            // WindowStyle.Hidden makes .NET pass STARTF_USESHOWWINDOW with SW_HIDE in the
            // STARTUPINFO, which is the request a GUI child honours for its first window.
            // The two flags cover different subsystems, so both are set deliberately.
            WindowStyle = ProcessWindowStyle.Hidden,

            // Our scripts JSON-escape non-ASCII, so this is belt-and-braces rather than a
            // fix for an observed failure - but the equivalent assumption did break the git
            // client on a path containing "ü", and CODESYS's own chatter is localised.
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

            ReportProgress(job, e.Data, stopwatch.Elapsed, progress);
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;

        // The exact command line, because CODESYS argument quoting is the single most
        // reliable source of "exits 1 in under a second having done nothing" and the only
        // way to diagnose it is to see what was actually passed.
        _log.Debug(LogCategory.Codesys, $"exec {codesysExecutablePath} {psi.Arguments}");

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(job.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            if (timedOut)
            {
                _log.Error(LogCategory.Codesys,
                    $"timed out after {job.Timeout} — killing the CODESYS process tree. A " +
                    "--noUI CODESYS sitting on a hidden modal never exits on its own");
            }
            else
            {
                _log.Warn(LogCategory.Codesys, "cancelled — killing the CODESYS process tree");
            }

            KillTree(process);

            if (!timedOut)
            {
                throw;
            }
        }

        // WaitForExitAsync can return before the async readers have flushed.
        await Task.Run(() => process.WaitForExit(), CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        string stdout;
        lock (gate)
        {
            stdout = buffer.ToString();
        }

        var output = ScriptProtocol.Parse(stdout);
        var missingProfile = output.Chatter.Any(ProfileResolver.IsMissingProfileError);
        var exitCode = timedOut ? -1 : SafeExitCode(process);

        _log.Log(exitCode == 0 && !timedOut ? LogLevel.Information : LogLevel.Error,
            LogCategory.Codesys,
            $"exit={exitCode} in {stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"events={output.Events.Count} warnings={output.Warnings.Count()}");

        if (missingProfile)
        {
            // A specific, recognisable failure with a specific fix, and one nobody guesses:
            // the profile string cannot be derived from the install folder name.
            _log.Error(LogCategory.Codesys,
                $"CODESYS rejected the profile '{job.Profile}'. The profile name is not the " +
                @"install folder name — read it from …\CODESYS\Profiles\*.profile.xml");
        }

        // What the script itself did, in order, before CODESYS's own chatter. This used to be
        // a single count - "events=24" - which said that something happened and nothing about
        // what. The export deletes an entire directory and writes a new one in its place; a
        // log that cannot show that step is not a log of the export.
        LogScriptEvents(job, output);

        // CODESYS's own output, kept at Debug. It is verbose and mostly uninteresting until
        // an export fails for a reason our protocol did not model, at which point it is the
        // only evidence there is.
        foreach (var line in output.Chatter)
        {
            _log.Debug(LogCategory.Codesys, $"› {line}");
        }

        return new CodesysResult
        {
            ExitCode = exitCode,
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed,
            Output = output,
            MissingProfile = missingProfile,
        };
    }

    private static void ReportProgress(
        CodesysJob job, string line, TimeSpan elapsed, IProgress<CodesysProgress>? progress)
    {
        if (progress is null || !line.Contains(ScriptProtocol.Marker, StringComparison.Ordinal))
        {
            return;
        }

        var parsed = ScriptProtocol.Parse(line);
        foreach (var evt in parsed.Events)
        {
            switch (evt.Type)
            {
                // One per object the export walks, carrying the name being worked on and a
                // denominator from the pre-count. `total` is absent when nobody counted, and
                // a null total is what the bar reads as indeterminate — so an old script and
                // a new host still agree.
                case "progress":
                    progress.Report(new CodesysProgress(job.OperationId, "working",
                        evt.Int("done"), evt.Int("total"), evt.String("obj"), elapsed));
                    break;
                case "phase":
                    progress.Report(new CodesysProgress(job.OperationId,
                        evt.String("name") ?? "phase", null, evt.Int("files"), null, elapsed));
                    break;
                case "opened":
                    progress.Report(new CodesysProgress(job.OperationId, "opened",
                        null, null, evt.String("path"), elapsed));
                    break;
            }
        }
    }


    /// <summary>
    /// Write the script's protocol events into the log as readable lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The events already drove progress, the changed-path list and the warning list, and were
    /// then thrown away. That left the two operations that can destroy work - swapping a new
    /// export over the old directory, and clearing a staging folder a killed run left behind -
    /// with no record anywhere, in a codebase whose stated rule is that a silent failure is a
    /// bug even when the code is correct.
    /// </para>
    /// <para>
    /// Levels are chosen by how many there are and who needs them. One line per exported
    /// object is Debug, because a real project has thousands. Skips, the summary and the
    /// filesystem swaps are Info: bounded in number, and each one answers a question somebody
    /// actually asks afterwards.
    /// </para>
    /// </remarks>
    private void LogScriptEvents(CodesysJob job, ScriptOutput output)
    {
        foreach (var evt in output.Events)
        {
            switch (evt.Type)
            {
                case "swap":
                    _log.Info(LogCategory.Codesys,
                        $"replacing the export directory {evt.String("out")}: " +
                        $"{evt.Int("replacing")} existing file(s) removed, " +
                        $"{evt.Int("with")} written in their place");
                    break;

                case "swapped":
                    _log.Info(LogCategory.Codesys,
                        $"export directory now holds {evt.Int("files")} file(s)");
                    break;

                case "staging-cleared":
                    // A staging folder that survived means a previous export was killed.
                    _log.Warn(LogCategory.Codesys,
                        $"cleared {evt.Int("files")} file(s) from a leftover staging folder " +
                        $"{evt.String("path")} — a previous export did not finish");
                    break;

                case "trash-cleared":
                    // Rarer than a leftover staging folder and worse: a trash directory
                    // survives only if a run died inside the three-rename swap, which is
                    // the one window in which the mirror could have looked wrong. Says
                    // where the previous export went, because that is what somebody
                    // needs if they arrived here looking for a mirror that vanished.
                    _log.Warn(LogCategory.Codesys,
                        $"cleared {evt.Int("files")} file(s) from {evt.String("path")} — " +
                        "a previous export was killed while swapping the new tree into place, " +
                        "and this was the export it was replacing");
                    break;

                case "skip":
                    _log.Info(LogCategory.Codesys,
                        $"not exported ({evt.String("reason")}): {evt.String("path")} " +
                        $"[{evt.String("type")}]");
                    break;

                case "libraries":
                    // Info, not Debug, and bounded: a project has one or two library
                    // managers, and this is the one export form whose entire point is
                    // content that used to be invisible. "0 references" is a real answer
                    // somebody may need to read rather than infer from a file.
                    _log.Info(LogCategory.Codesys,
                        $"library references recorded: {evt.Int("count")} " +
                        $"({evt.Int("system")} added by plug-ins) in {evt.String("path")}");
                    break;

                case "not_imported":
                    // An export-only file the import left alone. Logged rather than
                    // warned: it happens on every import of every project that has a
                    // library manager, and a warning that is always present is how a
                    // real one gets read past.
                    //
                    // The advice was hard-coded to "change this in the IDE", which was right
                    // while a library file was the only thing that reached here. A task
                    // configuration's XML has its importable half sitting beside it, and the
                    // script already says which file that is — so the line said the one thing
                    // guaranteed to send the reader past it.
                    _log.Info(LogCategory.Codesys,
                        $"not applied to the project ({evt.String("reason")}): " +
                        $"{evt.String("path")} — " +
                        (evt.String("sibling") is { Length: > 0 } sibling
                            ? $"edit {sibling} instead"
                            : "change this in the IDE"));
                    break;

                case "counted":
                    // The progress denominator, said once. Worth a line because it is the
                    // only place the two numbers in "walked N" can be checked against what
                    // the export expected to walk before it started — a large gap between
                    // them means the pre-count and the walk disagree about descent.
                    _log.Debug(LogCategory.Codesys,
                        $"expecting to walk {evt.Int("objects")} object(s)");
                    break;

                case "progress":
                    // Deliberately silent. It already drove the progress bar in
                    // ReportProgress, there is one per object walked, and the objects that
                    // produced a file are logged by "object" a few lines below. Letting it
                    // fall through to the default would double the Debug volume of every
                    // export to say nothing new.
                    break;

                case "summary":
                    // TWO SCRIPTS, TWO SETS OF FIELDS, and this used to read only the
                    // exporter's. An import reports updated / unchanged / native / created /
                    // missing / failed / partial / notImported, and `ScriptEvent.Int` returns
                    // null for a key that is not there - so every import in the application's
                    // history logged "walked , wrote , skipped  (of which  not implemented),
                    // 0 warning(s)". Four blanks and one number, and the number was the only
                    // true thing on the line because `warnings` happens to be common to both.
                    //
                    // Keyed on the JOB rather than on which keys are present. Sniffing the
                    // payload would have worked today and would silently pick a branch the
                    // day a third script grows a summary; a switch on the operation makes
                    // that a compiler error instead.
                    _log.Info(LogCategory.Codesys, job.Operation switch
                    {
                        CodesysOperation.Import => ImportSummary(evt),
                        _ => $"walked {evt.Int("walked")}, wrote {evt.Int("written")}, " +
                             $"skipped {evt.Int("skipped")} (of which {evt.Int("gaps")} " +
                             $"not implemented), {evt.Int("warnings")} warning(s)",
                    });
                    break;

                case "fatal":
                    _log.Error(LogCategory.Codesys, $"script failed: {evt.String("msg")}");
                    break;

                case "created":
                    _log.Info(LogCategory.Codesys,
                        $"created {evt.String("kind")} {evt.String("name")} " +
                        $"in {evt.String("into")}");
                    break;

                case "updated":
                    // One per file the import changed, so Debug for the same reason "object"
                    // is: bounded by what was edited today, but there is no ceiling on that.
                    _log.Debug(LogCategory.Codesys,
                        $"applied {evt.String("path")} ({evt.String("part")})");
                    break;

                case "native_imported":
                    _log.Debug(LogCategory.Codesys,
                        $"imported {evt.String("path")} as native XML into " +
                        $"{evt.String("into")}");
                    break;

                case "native_partial":
                    // WARN, and the only event in this switch that is. CODESYS does not raise
                    // when an individual object inside a native import fails - it hands the
                    // exception to a callback and carries on - so this is the difference
                    // between a file that was applied and one that was half applied, on a run
                    // that otherwise looks entirely successful.
                    _log.Warn(LogCategory.Codesys,
                        $"{evt.String("path")} was only partly applied: " +
                        $"{evt.Int("pasted")} object(s) imported, {evt.Int("errors")} failed, " +
                        $"{evt.Int("refused")} refused by CODESYS — the project has some of " +
                        "this file's objects and not others");
                    break;

                case "task_set":
                    // INFO, deliberately, where its neighbours are Debug. There are a handful
                    // of tasks in a project, so the volume argument that puts one-per-object
                    // lines at Debug does not apply - and this is Versio changing what the
                    // machine runs: a priority, an interval, or the CPU core a task is bound
                    // to. It logged as "· task_set" with no detail at all until 2026-08-15,
                    // which is a poor way to record the one write this whole path exists for.
                    _log.Info(LogCategory.Codesys,
                        $"set {evt.String("task")}.{evt.String("field")} = " +
                        $"{evt.String("value")} ({evt.String("path")})");
                    break;

                case "phase_saved":
                    _log.Debug(LogCategory.Codesys,
                        $"saved after the {evt.String("after")} phase");
                    break;

                case "saved":
                    _log.Info(LogCategory.Codesys, "project saved");
                    break;

                case "nothing_to_save":
                    // Info, because "I edited the mirror and nothing happened" is a question
                    // somebody asks, and this is the line that answers it.
                    _log.Info(LogCategory.Codesys,
                        "the project was not modified, so nothing was saved");
                    break;

                case "object":
                    _log.Debug(LogCategory.Codesys,
                        $"wrote {evt.String("path")} [{evt.String("form")}]");
                    break;

                default:
                    _log.Debug(LogCategory.Codesys, $"· {evt.Type}");
                    break;
            }
        }
    }

    /// <summary>The import script's own summary fields, in its own words.</summary>
    /// <remarks>
    /// <para>
    /// Built as a list and joined rather than as one interpolated sentence, because the
    /// interesting half of an import summary is usually zero. "0 failed" on every line is the
    /// noise that made the original blank line survive unnoticed for as long as it did, so the
    /// counts that are zero are simply absent, and a completely uneventful import says so in
    /// three words instead of eight numbers.
    /// </para>
    /// <para>
    /// <c>failed</c> and <c>partial</c> are always shown when non-zero and never merged.
    /// A partly-applied file is counted in both — <c>failed</c> is the file count and
    /// <c>partial</c> says how many of those still changed the project — and collapsing them
    /// would recreate the exact ambiguity this fix exists to remove.
    /// </para>
    /// </remarks>
    private static string ImportSummary(ScriptEvent evt)
    {
        List<string> parts = [];

        void Add(string label, string key)
        {
            var value = evt.Int(key);
            if (value is > 0)
            {
                parts.Add($"{value} {label}");
            }
        }

        Add("updated", "updated");
        Add("created", "created");
        Add("native", "native");
        Add("unchanged", "unchanged");
        Add("not applied (export-only)", "notImported");
        Add("missing", "missing");
        Add("FAILED", "failed");
        Add("partly applied", "partial");

        var warnings = evt.Int("warnings") ?? 0;
        return (parts.Count == 0 ? "import applied nothing" : "import: " + string.Join(", ", parts))
               + $" — {warnings} warning(s)";
    }

    /// <summary>
    /// Build the command line in the exact shape CODESYS accepts: quotes around each
    /// value, never around the whole <c>--name=value</c> pair.
    /// </summary>
    /// <remarks>
    /// A profile is omitted entirely when blank rather than passed as <c>--profile=""</c>,
    /// so the resulting failure is CODESYS's own "you must specify a profile" refusal —
    /// which the runner recognises and reports — instead of an opaque parse error.
    /// </remarks>
    internal static string BuildArguments(string profile, string scriptPath, string jobFile)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            parts.Add($"--profile=\"{profile}\"");
        }

        parts.Add("--noUI");
        parts.Add($"--runscript=\"{scriptPath}\"");
        parts.Add($"--scriptargs:\"{jobFile}\"");
        return string.Join(' ', parts);
    }

    private static string ScriptFor(CodesysOperation operation) => operation switch
    {
        CodesysOperation.Export => "versio_export.py",
        CodesysOperation.Import => "versio_import.py",
        CodesysOperation.Probe => "versio_probe.py",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    /// <summary>
    /// Read the mirror and decide what importing it would do, for a caller that did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The walk itself is not here.</b> It is <see cref="Versio.Core.Plc.ImportPlanReader"/>,
    /// shared with the orchestrator, which needs the same plan earlier and for a different
    /// reason: a confirmation dialog cannot name what an import will delete if the plan is not
    /// decided until CODESYS is already running. Two copies of that walk was the obvious shape
    /// and the wrong one — it is the arrangement CODESYS.md section 9 counts four drifts in,
    /// and this walk has a convention inside it (mirror-relative paths, never absolute) that
    /// is completely silent when got wrong.
    /// </para>
    /// <para>
    /// This entry point survives because a job can still arrive without a plan — a test
    /// driving the runner directly, or any caller below the orchestrator — and an import must
    /// be plan-driven whichever door it came through. The walk that inferred everything from
    /// file names is deleted, not bypassed.
    /// </para>
    /// </remarks>
    private Task<Versio.Core.Plc.ImportPlan> PlanImportAsync(
        string mirrorRoot, CancellationToken ct) =>
        Versio.Core.Plc.ImportPlanReader.ReadAsync(
            new FileSystem.WindowsFileSystem(),
            CodesysProjectFormat.Instance,
            mirrorRoot,
            _log,
            LogCategory.Codesys,

            // No binding id down here: this runner is handed paths, not bindings. The
            // orchestrator's call supplies one, which is why the reader takes a label rather
            // than assuming there is always something to name.
            label: string.Empty,
            ct);

    private static string WriteJobFile(CodesysJob job)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Versio", "jobs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"job-{job.OperationId}-{Guid.NewGuid():N}.json");

        // UTF-8 WITHOUT BOM. IronPython 2.7's json.load fails on a BOM with
        // "No JSON object could be decoded".
        File.WriteAllText(path, job.ToJson(), new UTF8Encoding(false));
        return path;
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill.
        }
        catch (NotSupportedException)
        {
            // Platform without tree-kill support; the single kill above is the best we can do.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover job file is harmless; failing the operation over it is not.
        }
    }
}
