using Versio.Core.Logging;
using Xunit.Abstractions;

namespace Versio.TestSupport;

/// <summary>
/// An <see cref="IVersioLogger"/> that writes into xunit's per-test output.
/// </summary>
/// <remarks>
/// <para>
/// The integration suite drives a real CODESYS and a real <c>git.exe</c>, so when one of
/// these tests fails the interesting evidence is the command line that ran and what it said
/// back. Routing the real logger into the test output means a failure report already
/// contains it, rather than requiring the test to be re-run by hand with tracing bolted on.
/// </para>
/// <para>
/// <see cref="ITestOutputHelper.WriteLine"/> throws once the test that owns it has finished,
/// which happens whenever a component outlives the test — a watcher timer, a process exit
/// callback. Those late writes are swallowed here: losing a trailing log line is a fair
/// price for not failing a passing test on a race in its own diagnostics.
/// </para>
/// </remarks>
public sealed class TestLogger(ITestOutputHelper output) : IVersioLogger
{
    private readonly List<LogEvent> _entries = [];
    private readonly Lock _gate = new();
    private string? _operationId;

    /// <summary>
    /// Everything logged so far, so a test can assert on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Anything that runs must say so in the log" had no test support at all</b> until
    /// 2026-08-17, which is why every rule about log volume in <c>CLAUDE.md</c> — quiet when
    /// idle, loud when working, a refused sync says nothing — was enforced by reading log
    /// files by hand. The first thing it caught was a <c>WRN</c> per branch at every launch
    /// for a state that is not a fault.
    /// </para>
    /// <para>
    /// A snapshot, and locked, because the components under test log from timers and process
    /// callbacks on other threads — enumerating the live list would throw in the middle of an
    /// assertion for reasons having nothing to do with the assertion.
    /// </para>
    /// </remarks>
    public IReadOnlyList<LogEvent> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Log(LogLevel level, string category, string message,
                    Exception? exception = null, bool isHeartbeat = false)
    {
        var entry = new LogEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            OperationId = _operationId,
            IsHeartbeat = isHeartbeat,
            Exception = exception,
        };

        lock (_gate)
        {
            _entries.Add(entry);
        }

        try
        {
            output.WriteLine(entry.Format());
        }
        catch (InvalidOperationException)
        {
            // The owning test has already completed. See the remarks above.
        }
    }

    public IDisposable BeginOperation(string operationId)
    {
        var previous = _operationId;
        _operationId = operationId;
        return new Scope(() => _operationId = previous);
    }

    public string? CurrentOperationId => _operationId;

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
