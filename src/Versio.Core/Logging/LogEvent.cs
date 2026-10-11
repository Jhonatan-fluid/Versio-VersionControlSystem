namespace Versio.Core.Logging;

/// <summary>
/// How much the reader is expected to care.
/// </summary>
/// <remarks>
/// Four levels, not six. <c>Trace</c> and <c>Critical</c> were left out because nothing here
/// could describe when to reach for them that <c>Debug</c> and <c>Error</c> did not already
/// cover, and a level nobody can define is a level that gets picked at random.
/// </remarks>
public enum LogLevel
{
    /// <summary>Detail that matters only once something has gone wrong: command lines,
    /// hashes, per-file decisions. On by default — an export is a rare event, and the volume
    /// buys the ability to explain one after the fact.</summary>
    Debug,

    /// <summary>The narrative. What the app did, in the order it did it.</summary>
    Information,

    /// <summary>Refused, retried or degraded. The app is still correct; the user may want to
    /// know why it did not do the obvious thing.</summary>
    Warning,

    /// <summary>An operation failed.</summary>
    Error,
}

/// <summary>Where a log line came from. Doubles as the UI filter.</summary>
public static class LogCategory
{
    /// <summary>Every button, menu and keyboard command, with the control that raised it.</summary>
    public const string Ui = "UI";

    /// <summary>Orchestration decisions: what was exported or imported, and what was refused.</summary>
    public const string Sync = "Sync";

    /// <summary>Direction-lock transitions, persistence and reboot recovery.</summary>
    public const string Lock = "Lock";

    /// <summary>
    /// Anything coming from a PLC toolchain, whichever vendor's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vendor-neutral because the sync engine is: a log filter that said "Codesys" would stop
    /// finding the interesting lines the moment a repository holds a TwinCAT project. The
    /// runner names the vendor in the message where it matters.
    /// </para>
    /// <para>
    /// <b>A vendor's own chatter gets its own category, declared by that vendor</b> — see
    /// <c>CodesysLog.Category</c>. It used to be a <c>Codesys</c> constant sitting right here,
    /// directly beneath the paragraph above explaining why it must not be, and above a second
    /// <c>&lt;summary&gt;</c> left behind when that paragraph was written. Neither survived
    /// contact with a seam test that reads constants.
    /// </para>
    /// </remarks>
    public const string Plc = "Plc";

    /// <summary>Everything driven through <c>git.exe</c>.</summary>
    public const string Git = "Git";

    /// <summary>Reads, writes and copies against the working tree.</summary>
    public const string Fs = "Fs";

    /// <summary>Change detection: watcher events, safety polls and overflow rescans. Kept
    /// apart from <see cref="Fs"/> so the poll chatter can be filtered out on its own.</summary>
    public const string Watch = "Watch";

    /// <summary>Durations and heartbeats. What turns "it felt slow" into a number.</summary>
    public const string Perf = "Perf";

    /// <summary>Process lifetime: startup, shutdown, configuration, unhandled failures.</summary>
    public const string App = "App";

    /// <summary>
    /// The web viewer's requests: what was asked for, of which repository, by whom, and how it
    /// ended. Also its audit trail of who viewed what (docs/web/PLAN.md, security rule 13).
    /// </summary>
    public const string Web = "Web";

    /// <summary>Signing in and out of the web viewer, token refreshes and refusals. Never a token.</summary>
    public const string Auth = "Auth";
}

/// <summary>
/// One log line, before anything decides how to render it.
/// </summary>
/// <remarks>
/// A record rather than a formatted string because the two consumers want different things:
/// the file sink wants <see cref="Format"/>, and the UI wants to filter and colour by
/// <see cref="Level"/>, <see cref="Category"/> and <see cref="OperationId"/> without
/// re-parsing text it just produced.
/// </remarks>
public sealed record LogEvent
{
    /// <summary>UTC, from <c>IClock</c> — never <c>DateTimeOffset.Now</c>, so tests can
    /// place entries in time and the file sink can roll on a deterministic day boundary.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    public required LogLevel Level { get; init; }

    /// <summary>One of the <see cref="LogCategory"/> constants.</summary>
    public required string Category { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// Correlates every line from one operation, across threads and across the CODESYS
    /// subprocess. Filtering to a single id is how you answer "why did that export do
    /// that" in seconds rather than minutes.
    /// </summary>
    public string? OperationId { get; init; }

    /// <summary>Set on heartbeat lines so the UI can collapse them into one updating row
    /// instead of burying the log in 300 identical entries.</summary>
    public bool IsHeartbeat { get; init; }

    public Exception? Exception { get; init; }

    /// <summary>Render for a text log. The exception, when present, follows on its own
    /// lines — a stack trace squeezed onto one line is a stack trace nobody reads.</summary>
    /// <remarks>
    /// <b>Rendered in LOCAL time, while <see cref="Timestamp"/> stays UTC.</b> The stored value
    /// is UTC so that ordering, rolling and tests are unambiguous; the rendered value is local
    /// because the person reading it is correlating against their own clock, the CODESYS log
    /// and the Windows event log. The file used to render UTC while the in-app log panel
    /// rendered local, so the same entry read as two different times depending on where you
    /// looked at it — the worst of both.
    /// </remarks>
    public string Format() =>
        $"{Timestamp.ToLocalTime():HH:mm:ss.fff} {Abbreviate(Level)} [{Category}] " +
        (OperationId is null ? string.Empty : $"{{{OperationId}}} ") +
        Message +
        (Exception is null ? string.Empty : $"\n{Exception}");

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };
}

/// <summary>
/// Somewhere a log entry ends up: the ring buffer the UI binds to, the rolling file.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe. Entries arrive from the UI thread, from the
/// heartbeat timer and from whichever pool thread an export resumed on, with no
/// synchronisation between them.
/// </remarks>
public interface ILogSink
{
    /// <summary>Accept one entry. May throw — <see cref="VersioLogger"/> catches, reports
    /// once and carries on, because a broken sink must not fail the operation it is
    /// describing.</summary>
    void Emit(LogEvent entry);
}

/// <summary>
/// The logging seam. Everything that logs takes this, never the concrete
/// <see cref="VersioLogger"/>.
/// </summary>
/// <remarks>
/// Two members only; the usual vocabulary (<c>Info</c>, <c>Warn</c>, …) arrives via
/// <see cref="LoggerExtensions"/>, so implementing this for a test or for
/// <see cref="NullLogger"/> stays trivial.
/// </remarks>
public interface IVersioLogger
{
    /// <param name="isHeartbeat">Marks a line the UI should collapse rather than append.</param>
    void Log(LogLevel level, string category, string message,
             Exception? exception = null, bool isHeartbeat = false);

    /// <summary>Open a correlation scope. Every line logged inside it carries the id,
    /// including lines written after an <c>await</c> on another thread.</summary>
    IDisposable BeginOperation(string operationId);

    /// <summary>
    /// The id lines are currently being stamped with, or <c>null</c> outside any scope.
    /// </summary>
    /// <remarks>
    /// Lets a component that may be called either from a user action or from a background
    /// trigger join the caller's correlation rather than starting a competing one — see
    /// <see cref="LoggerExtensions.JoinOrBeginOperation"/>. Also worth quoting in an error
    /// message, so the user has something to search the log file for.
    /// </remarks>
    string? CurrentOperationId { get; }
}
