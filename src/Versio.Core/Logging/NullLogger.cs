namespace Versio.Core.Logging;

/// <summary>
/// An <see cref="IVersioLogger"/> that discards everything.
/// </summary>
/// <remarks>
/// <para>
/// Exists so that components can take a <b>non-nullable</b> logger with
/// <c>logger ?? NullLogger.Instance</c> at the constructor and then log unconditionally.
/// The alternative — a nullable field and <c>_logger?.Info(…)</c> at every call site — does
/// not even compile against the extension methods in <see cref="LoggerExtensions"/>, and
/// invites the habit of quietly skipping a log line rather than wiring a logger through.
/// </para>
/// <para>
/// Unit tests are the main beneficiary: hundreds of them construct these components and
/// none of them care about logging, so the logger stays an optional argument they never
/// mention.
/// </para>
/// </remarks>
public sealed class NullLogger : IVersioLogger
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static readonly NullLogger Instance = new();

    private NullLogger()
    {
    }

    public void Log(LogLevel level, string category, string message,
                    Exception? exception = null, bool isHeartbeat = false)
    {
        // Intentionally empty.
    }

    /// <summary>Returns a scope that does nothing, so <c>using</c> blocks around it still
    /// read the same at the call site.</summary>
    public IDisposable BeginOperation(string operationId) => NoScope.Instance;

    /// <summary>Always <c>null</c>. Nothing is being correlated because nothing is being
    /// recorded.</summary>
    public string? CurrentOperationId => null;

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
            // Intentionally empty.
        }
    }
}
