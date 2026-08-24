using System.Diagnostics;
using System.IO;
using System.Windows;
using Versio.App.Spikes;
using Versio.App.Theme;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Logging;
using Versio.Infrastructure.Platform;
using Versio.Plc.Codesys.Infrastructure;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.Plc.TwinCat.Infrastructure;

namespace Versio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        TraceBindingsIfRequested(e.Args);

        // Before the first window exists, so the shell's caption is painted the moment it
        // loads rather than a frame later. One handler covers every window the app will ever
        // open - the dialogs, System information, the spike - including ones not written yet.
        WindowCaption.FollowTheme();

        // Follow the Windows app theme until the settings file says otherwise - which it is
        // read for below, once there is a store to read it from. Applied here as well as
        // there so the spike and any early failure are painted rather than raw.
        //
        // AND KEEP FOLLOWING IT. "Follow system" was a snapshot: the registry was read once
        // and nothing listened afterwards, so flipping Windows into dark mode with Versio open
        // left it light until restart - on the setting that is the default and that says by
        // name that it follows.
        ThemeManager.Apply(AppTheme.System);
        ThemeManager.FollowSystemChanges();

        // Before the file sink: leftover settings/logs still sit in the Velopack
        // directory from builds that used that folder as AppData. Copy them out so
        // today's log and the recent list are not inside a tree uninstall will delete.
        AppUserPaths.MigrateFromLegacyInstallDirectory();

        // --spike runs the R25 tree measurement instead of the shell. Kept in the shipped
        // app rather than deleted, so the virtualization settings have a regression check
        // that anyone can run.
        if (e.Args.Contains("--spike", StringComparer.OrdinalIgnoreCase))
        {
            var spike = new TreePerformanceSpike();
            if (e.Args.Contains("--measure", StringComparer.OrdinalIgnoreCase))
            {
                spike.RunHeadless();
            }

            spike.Show();
            return;
        }

        var clock = new SystemClock();
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(clock).AddSink(sink);

        // The file first, so that everything below it is on disk. Attaching this when a
        // repository opened — as it used to — meant the banner and any startup crash were
        // recorded only in memory, and memory does not survive the crash.
        var fileSink = AttachFileLog(logger);
        Exit += (_, _) => fileSink?.Dispose();

        // Before anything else can fail. A crash during startup is exactly the crash with
        // no user sitting in front of it to describe what they were doing.
        InstallCrashHandlers(logger);
        LogStartupBanner(logger, e.Args);

        // WHICH VENDORS THIS BUILD HAS. The lines in the application that name a toolchain.
        //
        // It used to be a hardcoded entry inside PlcFormats and another inside PlcToolchains,
        // which put a vendor's name in Versio.Core - the assembly the architectural rule exists
        // to keep free of exactly that - and made "adding a vendor means registering" a
        // description of something nothing ever did. Adding a vendor is one reference in the
        // csproj, one Register() call here, and no change anywhere above it.
        //
        // CODESYS stays first so a missing vendor field on an old binding still deserialises
        // to the first vendor — history, not a session pin. The session probes every
        // registered vendor; it used to take PlcFormats.All[0] and never look again.
        //
        // Registered before the shell, because opening a repository scans it for every known
        // format and a shell built first would scan for none.
        CodesysPack.Register();
        TiaPortalPack.Register();
        TwinCatPack.Register();
        logger.Info(LogCategory.App,
            $"vendors registered: {string.Join(", ", PlcFormats.All.Select(f => f.DisplayName))}");

        // True children (CODESYS.exe, git.exe) die with Versio. Host.exe is not assigned:
        // killing it before Quit/Dispose is how Portal and XAE survive a parent death.
        ChildProcess.Session = new ChildProcess.KillOnCloseJob();
        Exit += (_, _) =>
        {
            ChildProcess.Session?.Dispose();
            ChildProcess.Session = null;
        };

        if (fileSink is not null)
        {
            logger.Info(LogCategory.App, $"logging to {fileSink.CurrentFile}");
        }

        // The file sink is already attached, above. The panel reads the same logger
        // through the ring buffer; both have been receiving every line since startup.
        var log = new LogViewModel(sink);

        // The real settings store is wired HERE and nowhere else. The view model defaults to
        // one that remembers nothing, so that constructing a shell - in a test, in a harness -
        // cannot rewrite the recent list belonging to whoever is running it.
        var settings = new JsonAppSettingsStore(logger: logger);

        // READ BEFORE THE FIRST WINDOW EXISTS, and blocking.
        //
        // The theme and the panel sizes both have to be in hand before anything is shown, or
        // the window paints a frame in the wrong palette at the wrong size and then jumps.
        // The shell reads the same file again for the recent list, which is a few milliseconds
        // of a small local file and keeps the shell's own contract untouched.
        //
        // Blocking is safe specifically because the store awaits with ConfigureAwait(false)
        // throughout: there is no dispatcher pumping inside OnStartup, and a continuation
        // posted back to it would deadlock.
        var stored = settings.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        ThemeManager.Apply(ThemeManager.Parse(stored.Theme));

        var shell = new ShellViewModel(logger, log, settings: settings)
        {
            // So "Open logs folder" opens where the logs actually are, not where they would
            // have gone had the sink attached successfully.
            FileLogDirectory = fileSink?.LogDirectory,
        };

        // The shell holds a filesystem watcher on the open repository, and a live
        // FileSystemWatcher keeps an OS handle on the directory. Releasing it on exit keeps
        // the folder free to be renamed or deleted the moment Versio closes.
        Exit += (_, _) => shell.Dispose();

        // Logging off or shutting down. This does NOT go through the close guard: Windows
        // gives a process a few seconds and will not read a dialog, so the only useful thing
        // to do is give up deliberately rather than be killed mid-run — which at least gets
        // the runner to kill the CODESYS process tree it started instead of orphaning it.
        //
        // Not vetoed, though SessionEndingCancelEventArgs would allow it. An application that
        // blocks a shutdown to protect its own work is one people learn to force-quit, and the
        // direction locks are already on disk and recovered on the next open.
        SessionEnding += (_, args) =>
        {
            logger.Warn(LogCategory.App,
                $"the session is ending ({args.ReasonSessionEnding})");
            shell.AbandonForShutdown();
        };

        new MainWindow(shell, stored.Layout).Show();

        // Is git there at all? Asked once, here, rather than at first use.
        //
        // The machine that needs this answered is the one where opening a repository is what
        // fails - and before this, the only thing that asked was the System Info window, which
        // answered "not checked - open a repository" when none was open. So the one question a
        // git-less machine needs was the one question it could not get to. Everything else
        // surfaced the absence as a Win32Exception from somewhere deep, which reads as a bug in
        // Versio rather than as a missing dependency.
        //
        // Not awaited, and deliberately not blocking the window: the probe is a process spawn,
        // and a splash-free app that pauses before painting looks broken. The panel shows
        // "Checking for git…" until this lands, which is why GitPresence has an Unknown state
        // distinct from Missing.
        _ = shell.ProbeGitAsync(CancellationToken.None);

        // The recent list first, and before anything opens: OpenAsync promotes a repository
        // to the top of it, so loading afterwards would overwrite the entry just opened - and
        // the startup fallback has nothing to fall back to until it has been read.
        _ = shell.LoadRecentAsync()
            .ContinueWith(
                _ => StartupOpen(shell, e.Args, logger),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Open a repository straight away when one is obvious.
    /// </summary>
    /// <remarks>
    /// An explicit path argument wins, then the working directory, then the most recent
    /// repository that is still on disk. The last of those is what makes a shortcut on the
    /// desktop useful: launched that way the working directory is wherever Windows put it,
    /// which is not a repository, and the app used to open on an empty window with a dropdown
    /// that had forgotten every repository the user had ever opened.
    /// </remarks>
    private static void StartupOpen(ShellViewModel shell, string[] args, VersioLogger logger)
    {
        var candidate = RepositoryArgument(args)
                        ?? shell.WorkingTreeRootOf(Directory.GetCurrentDirectory())
                        ?? shell.MostRecentExisting();

        if (candidate is not null && Directory.Exists(candidate))
        {
            // Fire-and-forget, but never silently: an exception inside an un-awaited Task is
            // swallowed by default, so the app would open on an empty window with no
            // explanation anywhere for the repository that failed to load.
            _ = shell.OpenAsync(candidate).ContinueWith(
                t => logger.Error(LogCategory.App,
                    $"failed to open {candidate} at startup", t.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        else
        {
            logger.Info(LogCategory.App,
                candidate is null
                    ? "no repository opened at startup: nothing obvious to open, and no " +
                      "remembered repository is still on disk"
                    : $"no repository opened at startup: {candidate} does not exist");
        }
    }

    /// <summary>
    /// Start writing the log to a file under the current user's local app data.
    /// </summary>
    /// <remarks>
    /// Failure here is reported, never fatal. A locked or unwritable folder costs the user
    /// their log file, which is bad; refusing to start the app over it would be worse, and
    /// the ring buffer behind the log panel keeps working either way.
    /// </remarks>
    private static RollingFileSink? AttachFileLog(VersioLogger logger)
    {
        try
        {
            var sink = new RollingFileSink();
            logger.AddSink(sink);
            return sink;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error(LogCategory.App,
                $"could not open a log file in {RollingFileSink.DefaultDirectory}; this " +
                "session will not be written to disk", ex);
            return null;
        }
    }

    /// <summary>
    /// Route every unhandled failure into the log before the process is allowed to die.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three separate channels, because .NET has three and each one loses a different class
    /// of failure:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Dispatcher</b> — an exception on the UI thread, which is where every
    ///     command handler runs. Marked handled so the window survives a failure in one
    ///     panel; the alternative is that a broken diff view takes the whole app down.</item>
    ///   <item><b>AppDomain</b> — anything on a background thread. Cannot be marked handled;
    ///     the process is going down regardless, so the only job is to get the line onto
    ///     disk first.</item>
    ///   <item><b>UnobservedTaskException</b> — a faulted <c>Task</c> nobody awaited. These
    ///     are silent by default in .NET, which makes them the failures most likely to be
    ///     reported as "it just didn't do anything".</item>
    /// </list>
    /// <para>
    /// The file sink is attached at startup, before these handlers, so a crash during
    /// construction still reaches disk. It used to wait for a repository to open, which
    /// meant the banner and any startup failure lived only in the ring buffer — and
    /// memory does not survive the crash these handlers exist to record.
    /// </para>
    /// </remarks>
    private void InstallCrashHandlers(VersioLogger logger)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error(LogCategory.App,
                $"unhandled exception on the UI thread: {args.Exception.Message}",
                args.Exception);

            // Handled, so the window stays up. The user keeps whatever unsynced work the
            // shell is holding, which matters more than a tidy shutdown.
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            logger.Error(LogCategory.App,
                $"unhandled exception on a background thread; the process is terminating " +
                $"(runtime says IsTerminating={args.IsTerminating})",
                ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Error(LogCategory.App,
                "a background task failed and nobody was awaiting it", args.Exception);

            // Observed, so the exception does not resurface. It has been recorded, which is
            // the only thing that was missing.
            args.SetObserved();
        };
    }

    /// <summary>
    /// The first lines of every session: what is running, where, and on what.
    /// </summary>
    /// <remarks>
    /// Every one of these has been the answer to a support question at some point — the
    /// wrong build, a per-user .NET install, a working directory nobody expected, a flag
    /// left in a shortcut. Writing them unconditionally costs one line each and removes a
    /// round trip asking the user to check.
    /// </remarks>
    private static void LogStartupBanner(VersioLogger logger, string[] args)
    {
        logger.Info(LogCategory.App, $"Versio {AppVersion.Display} starting");
        logger.Info(LogCategory.App,
            $"{Environment.OSVersion.VersionString}, .NET {Environment.Version}, " +
            $"{(Environment.Is64BitProcess ? "x64" : "x86")} process, " +
            $"{Environment.ProcessorCount} cores");
        logger.Info(LogCategory.App,
            $"running as {Environment.UserName}@{Environment.MachineName}, pid {Environment.ProcessId}");
        logger.Debug(LogCategory.App, $"executable {Environment.ProcessPath}");
        logger.Debug(LogCategory.App, $"working directory {Directory.GetCurrentDirectory()}");
        logger.Debug(LogCategory.App,
            args.Length == 0 ? "no command-line arguments" : $"arguments: {string.Join(' ', args)}");
    }

    /// <summary>
    /// The repository path from the command line, if there is one.
    /// </summary>
    /// <remarks>
    /// <b>The value after a flag is not a repository.</b> This used to take the first argument
    /// that did not start with <c>--</c>, which meant
    /// <c>Versio.exe --trace-bindings out.txt C:\repo</c> tried to open <c>out.txt</c> and
    /// silently started with no repository open. The failure was quiet in the worst way: the
    /// binding tracer it was written for reported a clean run over panels that had never been
    /// populated, so a diagnostic run proved far less than it appeared to.
    /// <para>
    /// Flags that take a value are listed rather than inferred. Guessing — "skip the next
    /// argument after any flag" — would break the moment a flag without a value is followed by
    /// the path, which is exactly how <c>--spike</c> is used.
    /// </para>
    /// </remarks>
    internal static string? RepositoryArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (ValueFlags.Contains(arg))
            {
                i++;                                  // its value, not a path
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                return arg;
            }
        }

        return null;
    }

    /// <summary>Command-line flags that consume the argument after them.</summary>
    private static readonly HashSet<string> ValueFlags =
        new(StringComparer.OrdinalIgnoreCase) { "--trace-bindings" };

    /// <summary>
    /// <c>--trace-bindings &lt;file&gt;</c>: write every WPF data-binding failure to a file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A broken binding in WPF is <b>silent</b>. A mistyped path, a missing converter or a
    /// property that never raises <c>PropertyChanged</c> produces an empty control and no
    /// error anywhere — the screen simply looks slightly wrong, and only if somebody happens
    /// to be looking at that screen. Every other failure in this codebase is loud; this one
    /// class of failure was the exception, and only because the diagnostic normally requires
    /// attaching a debugger.
    /// </para>
    /// <para>
    /// Off unless asked for: the listener costs a string format on every binding evaluation,
    /// which is not something to carry through a tree of five thousand nodes.
    /// </para>
    /// </remarks>
    private static void TraceBindingsIfRequested(string[] args)
    {
        var flag = Array.FindIndex(args, a =>
            a.Equals("--trace-bindings", StringComparison.OrdinalIgnoreCase));
        if (flag < 0 || flag + 1 >= args.Length)
        {
            return;
        }

        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new TextWriterTraceListener(args[flag + 1]) { TraceOutputOptions = TraceOptions.None });
        source.Switch.Level = SourceLevels.Warning;
        Trace.AutoFlush = true;
    }
}
