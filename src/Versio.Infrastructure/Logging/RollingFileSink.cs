using System.Text;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Logging;

/// <summary>
/// Appends log entries to a daily file under <c>.versio/local/logs/</c>.
/// </summary>
/// <remarks>
/// Writes are serialised and flushed per entry. Buffering would be faster, but the entries
/// that matter most are the ones written immediately before a crash — and those are exactly
/// the ones a buffer loses.
/// </remarks>
public sealed class RollingFileSink : ILogSink, IDisposable
{
    private readonly string _directory;
    private readonly int _retainDays;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _openFor;

    public RollingFileSink(string repositoryRoot, int retainDays = 7)
    {
        _directory = Path.Combine(repositoryRoot, ".versio", "local", "logs");
        _retainDays = retainDays;
        Directory.CreateDirectory(_directory);
        Prune();
    }

    public void Emit(LogEvent entry)
    {
        lock (_gate)
        {
            var day = DateOnly.FromDateTime(entry.Timestamp.UtcDateTime);
            if (_writer is null || day != _openFor)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(
                    Path.Combine(_directory, $"versio-{day:yyyy-MM-dd}.log"),
                    append: true,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                _openFor = day;
            }

            _writer.WriteLine(entry.Format());
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "versio-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // In use, or being read. It will be pruned next launch.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
