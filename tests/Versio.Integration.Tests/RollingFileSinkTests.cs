using System.Text;
using Versio.Core.Logging;
using Versio.Infrastructure.Logging;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The log file itself.
/// </summary>
/// <remarks>
/// These exist because the sink was written, tested by eye, and then never constructed by
/// anything — so for a long time nothing was ever written to disk at all while the app
/// looked, from the log panel, as though logging worked. Asserting on real bytes in a real
/// file is the only version of this test that would have caught that.
/// </remarks>
public sealed class RollingFileSinkTests : IDisposable
{
    /// <summary>A throwaway stand-in for <c>%LOCALAPPDATA%\Versio\logs</c>. The real
    /// directory is asserted on separately, without writing to it.</summary>
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "versio-sink-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// Read the log while the sink still holds it open.
    /// </summary>
    /// <remarks>
    /// <see cref="FileShare.ReadWrite"/> is required, not optional: the sink keeps the file
    /// open for writing, and a reader that does not tolerate an existing write handle is
    /// refused outright. <see cref="File.ReadAllText(string)"/> is such a reader — which is
    /// worth knowing, because reading the live log is exactly what a support request asks
    /// someone to do.
    /// </remarks>
    private byte[] LogBytes()
    {
        using var buffer = new MemoryStream();
        foreach (var file in Directory.EnumerateFiles(_dir, "versio-*.log"))
        {
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.CopyTo(buffer);
        }

        return buffer.ToArray();
    }

    private string LogText() => Encoding.UTF8.GetString(LogBytes());

    [Fact]
    public void An_entry_reaches_the_file()
    {
        using var sink = new RollingFileSink(_dir);

        sink.Emit(Entry(LogLevel.Information, "exported 3 objects"));

        Assert.Contains("exported 3 objects", LogText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_are_flushed_immediately_rather_than_buffered()
    {
        // The entries that matter most are the ones written immediately before a crash, and
        // those are exactly the ones a buffer loses. Read without disposing the sink.
        var sink = new RollingFileSink(_dir);
        try
        {
            sink.Emit(Entry(LogLevel.Error, "about to crash"));

            Assert.Contains("about to crash", LogText(), StringComparison.Ordinal);
        }
        finally
        {
            sink.Dispose();
        }
    }

    [Fact]
    public void The_file_is_utf8_without_a_bom()
    {
        // A BOM is the recurring encoding bug in this codebase, and a log file gets opened
        // by every text tool the user owns.
        using var sink = new RollingFileSink(_dir);

        sink.Emit(Entry(LogLevel.Information, "Kühlschrank Regelung.st"));

        var bytes = LogBytes();

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the log file starts with a UTF-8 BOM");
        Assert.Contains("Kühlschrank", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_is_written_with_its_stack()
    {
        using var sink = new RollingFileSink(_dir);

        Exception captured;
        try
        {
            throw new InvalidOperationException("the export did not complete");
        }
        catch (InvalidOperationException ex)
        {
            captured = ex;
        }

        sink.Emit(Entry(LogLevel.Error, "export failed", captured));

        var text = LogText();
        Assert.Contains("the export did not complete", text, StringComparison.Ordinal);
        Assert.Contains(nameof(An_exception_is_written_with_its_stack), text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_correlation_id_survives_into_the_file()
    {
        // Filtering a support log to one id is the point of having ids at all.
        using var sink = new RollingFileSink(_dir);

        sink.Emit(Entry(LogLevel.Information, "exporting") with { OperationId = "7f3a1c2b" });

        Assert.Contains("{7f3a1c2b}", LogText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Old_files_are_pruned_on_construction()
    {
        var dir = Directory.CreateDirectory(_dir);
        var stale = Path.Combine(dir.FullName, "versio-2020-01-01.log");
        var fresh = Path.Combine(dir.FullName, "versio-2020-01-02.log");
        File.WriteAllText(stale, "old");
        File.WriteAllText(fresh, "new");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        using var sink = new RollingFileSink(_dir, retainDays: 5);

        Assert.False(File.Exists(stale), "a month-old log file was not pruned");
        Assert.True(File.Exists(fresh), "a current log file was pruned");
    }

    [Fact]
    public void Exactly_five_days_are_kept_by_default()
    {
        var dir = Directory.CreateDirectory(_dir);
        var kept = new List<string>();
        var dropped = new List<string>();

        // One file per day going back a fortnight, dated by last-write time the way real
        // ones are.
        for (var age = 0; age < 14; age++)
        {
            var day = DateTime.UtcNow.AddDays(-age);
            var file = Path.Combine(dir.FullName, $"versio-{day:yyyy-MM-dd}.log");
            File.WriteAllText(file, $"day -{age}");
            File.SetLastWriteTimeUtc(file, day);
            (age < RollingFileSink.DefaultRetainDays ? kept : dropped).Add(file);
        }

        using var sink = new RollingFileSink(_dir);

        Assert.All(kept, f => Assert.True(File.Exists(f), $"{Path.GetFileName(f)} was pruned"));
        Assert.All(dropped, f => Assert.False(File.Exists(f), $"{Path.GetFileName(f)} was kept"));
    }

    [Fact]
    public void A_file_is_opened_per_day_and_named_for_that_day()
    {
        // The date in the name is what makes "it broke on Tuesday" a file rather than a
        // search, so it tracks the entry's own timestamp, not the wall clock.
        using var sink = new RollingFileSink(_dir);

        sink.Emit(Entry(LogLevel.Information, "monday") with
        {
            Timestamp = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero),
        });
        sink.Emit(Entry(LogLevel.Information, "tuesday") with
        {
            Timestamp = new DateTimeOffset(2026, 3, 3, 9, 0, 0, TimeSpan.Zero),
        });

        var monday = Path.Combine(_dir, "versio-2026-03-02.log");
        var tuesday = Path.Combine(_dir, "versio-2026-03-03.log");

        Assert.Contains("monday", ReadShared(monday), StringComparison.Ordinal);
        Assert.Contains("tuesday", ReadShared(tuesday), StringComparison.Ordinal);
        Assert.DoesNotContain("tuesday", ReadShared(monday), StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_directory_is_under_local_app_data_not_roaming()
    {
        // Roaming would copy machine-specific diagnostics across every machine the user
        // signs into, and lengthen every logon to do it.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(local, RollingFileSink.DefaultDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Versio", "logs"), RollingFileSink.DefaultDirectory,
                        StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(local, roaming, StringComparison.OrdinalIgnoreCase))
        {
            Assert.DoesNotContain(roaming, RollingFileSink.DefaultDirectory,
                                  StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_current_file_is_reported_so_it_can_be_named_in_the_log()
    {
        using var sink = new RollingFileSink(_dir);

        sink.Emit(Entry(LogLevel.Information, "anything"));

        Assert.Equal(_dir, Path.GetDirectoryName(sink.CurrentFile));
        Assert.True(File.Exists(sink.CurrentFile), "the reported current file does not exist");
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_log_directory_is_reported_so_the_app_can_tell_the_user_where_it_is()
    {
        using var sink = new RollingFileSink(_dir);

        Assert.Equal(_dir, sink.LogDirectory);
        Assert.True(Directory.Exists(sink.LogDirectory));
    }

    private static LogEvent Entry(LogLevel level, string message, Exception? exception = null) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = level,
        Category = LogCategory.Sync,
        Message = message,
        Exception = exception,
    };
}
