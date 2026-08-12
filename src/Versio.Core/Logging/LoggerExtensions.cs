namespace Versio.Core.Logging;

/// <summary>
/// The convenience vocabulary for <see cref="IVersioLogger"/>.
/// </summary>
/// <remarks>
/// <para>
/// Extension methods rather than interface members, on purpose. Anyone implementing
/// <see cref="IVersioLogger"/> — a test double, <see cref="NullLogger"/>, a future sink
/// router — has exactly two methods to write, while every caller still gets the full
/// vocabulary. The alternative, putting these on the concrete <see cref="VersioLogger"/>,
/// is what forced <c>Versio.App</c> to depend on the class rather than the seam and left
/// <c>Versio.Infrastructure</c> passing <c>Action&lt;string&gt;</c> callbacks that threw
/// away level, category and exception.
/// </para>
/// <para>
/// None of these are null-tolerant, and that is deliberate: components take a non-nullable
/// <see cref="IVersioLogger"/> defaulting to <see cref="NullLogger.Instance"/>, so no call
/// site needs a <c>?.</c> — which would not compile against an extension method anyway.
/// </para>
/// </remarks>
public static class LoggerExtensions
{
    /// <summary>Detail that matters only when something has already gone wrong: command
    /// lines, hashes, per-file decisions.</summary>
    public static void Debug(this IVersioLogger logger, string category, string message) =>
        logger.Log(LogLevel.Debug, category, message);

    /// <summary>The narrative an engineer reads to understand what the app did.</summary>
    public static void Info(this IVersioLogger logger, string category, string message) =>
        logger.Log(LogLevel.Information, category, message);

    /// <summary>Something was refused, retried or degraded, and the user may need to know.</summary>
    public static void Warn(this IVersioLogger logger, string category, string message) =>
        logger.Log(LogLevel.Warning, category, message);

    /// <summary>An operation failed. Pass the exception whenever there is one — the stack is
    /// appended by <see cref="LogEvent.Format"/> and is usually the whole answer.</summary>
    public static void Error(this IVersioLogger logger, string category, string message,
                             Exception? exception = null) =>
        logger.Log(LogLevel.Error, category, message, exception);

    /// <summary>
    /// Log a UI interaction: which control, addressed by path.
    /// </summary>
    /// <remarks>
    /// Satisfies "every button press is logged" without a line in every handler. The path is
    /// written <c>Panel.Control</c> so a support log can be read as a click trail.
    /// </remarks>
    public static void Click(this IVersioLogger logger, string controlPath) =>
        logger.Log(LogLevel.Information, LogCategory.Ui, $"Click ▸ {controlPath}");

    /// <summary>
    /// Open a correlation scope under a freshly generated id, and hand the id back.
    /// </summary>
    /// <remarks>
    /// The caller almost always needs the id as well as the scope — to name a CODESYS job,
    /// or to quote in an error the user can search the log for. This spares every call site
    /// the two-line dance of generating one and passing it separately.
    /// </remarks>
    public static IDisposable BeginOperation(this IVersioLogger logger, out string operationId)
    {
        operationId = NewOperationId();
        return logger.BeginOperation(operationId);
    }

    /// <summary>
    /// Join the correlation scope already in progress, or start a fresh one if there is none.
    /// </summary>
    /// <remarks>
    /// For components reachable from two directions. <see cref="Sync.SyncOrchestrator"/> is
    /// the case: called from a button press it should inherit that click's id, but called
    /// from a file watcher there is no ambient scope and its lines would otherwise be the
    /// only uncorrelated ones in the file — precisely the events hardest to reconstruct
    /// afterwards.
    /// </remarks>
    public static IDisposable JoinOrBeginOperation(this IVersioLogger logger,
                                                   out string operationId)
    {
        operationId = logger.CurrentOperationId ?? NewOperationId();
        return logger.BeginOperation(operationId);
    }

    /// <summary>
    /// A short, unique-enough id for one operation.
    /// </summary>
    /// <remarks>
    /// Eight hex characters. Long enough that two concurrent operations will not collide,
    /// short enough to sit in every log line without pushing the message off the screen —
    /// and short enough that a user can read one out over the phone.
    /// </remarks>
    public static string NewOperationId() => Guid.NewGuid().ToString("N")[..8];
}
