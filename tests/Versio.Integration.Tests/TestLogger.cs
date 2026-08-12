using Versio.Core.Logging;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

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
    private string? _operationId;

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
