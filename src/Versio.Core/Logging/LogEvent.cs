namespace Versio.Core.Logging;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>Where a log line came from. Doubles as the UI filter.</summary>
public static class LogCategory
{
    /// <summary>Every button, menu and keyboard command, with the control that raised it.</summary>
    public const string Ui = "UI";

    public const string Sync = "Sync";
    public const string Lock = "Lock";
    public const string Codesys = "Codesys";
    public const string Git = "Git";
    public const string Fs = "Fs";
    public const string Perf = "Perf";
    public const string App = "App";
}

public sealed record LogEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required LogLevel Level { get; init; }
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

    public string Format() =>
        $"{Timestamp:HH:mm:ss.fff} {Abbreviate(Level)} [{Category}] " +
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

public interface ILogSink
{
    void Emit(LogEvent entry);
}

public interface IVersioLogger
{
    void Log(LogLevel level, string category, string message,
             Exception? exception = null, bool isHeartbeat = false);

    /// <summary>Open a correlation scope. Every line logged inside it carries the id.</summary>
    IDisposable BeginOperation(string operationId);
}
