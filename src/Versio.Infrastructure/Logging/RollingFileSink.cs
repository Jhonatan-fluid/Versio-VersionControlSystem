using System.Text;
using Versio.Core.Logging;
using Versio.Infrastructure.FileSystem;

namespace Versio.Infrastructure.Logging;

/// <summary>
/// Appends log entries to a daily text file the user can open, read and send on.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per day, five days kept.</b> A date in the name is what makes "it broke on
/// Tuesday" actionable without a log viewer, and a fixed window means the folder cannot grow
/// without bound on a machine nobody ever tidies.
/// </para>
/// <para>
/// <b>Under <c>%LOCALAPPDATA%</c>, not the repository.</b> Three reasons, in order of how
/// much they cost when ignored: a repository is not open yet during startup, so a crash
/// before one is chosen would never be recorded anywhere; a repository can be read-only, on
/// a network share, or deleted while the log is the only evidence of what happened to it;
/// and one machine-wide location means there is a single place to look rather than one per
/// repository. See <see cref="DefaultDirectory"/> for why Local rather than Roaming.
/// </para>
/// <para>
/// Writes are serialised and flushed per entry. Buffering would be faster, but the entries
/// that matter most are the ones written immediately before a crash — and those are exactly
/// the ones a buffer loses.
/// </para>
/// <para>
/// <b>One process per file.</b> Two Versio windows are allowed at once — on two repositories —
/// and both used to aim at the same <c>versio-&lt;date&gt;.log</c>. The first one holds it
/// open for writing, so the second's every write failed: the logger reported the dead sink
/// once, in the second window's own log panel, and nothing that window did reached disk.
/// Found on 2026-09-22 when a second launch's log line could not be found anywhere. The
/// second process now writes <c>versio-&lt;date&gt;-&lt;pid&gt;.log</c> beside it, rather than
/// sharing the file: two processes appending to one file interleave their lines with nothing
/// saying which line is whose.
/// </para>
/// </remarks>
public sealed class RollingFileSink : ILogSink, IDisposable
{
    /// <summary>How many days of files to keep, including today.</summary>
    public const int DefaultRetainDays = 5;

    private readonly string _directory;
    private readonly int _retainDays;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _openFor;
    private string _openPath = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Where logs live for the current user: <c>%LOCALAPPDATA%\VersioUser\logs</c>.
    /// </summary>
    /// <remarks>
    /// <b>Local, deliberately, not Roaming.</b> On a domain with roaming profiles everything
    /// under <c>%APPDATA%</c> is copied to the server at logoff and back at logon. Diagnostic
    /// logs are machine-specific by definition, so roaming them would slow every sign-in to
    /// carry data that is meaningless on the other machine — and this audience is far more
    /// likely than most to be on exactly that kind of managed domain.
    /// <para>
    /// Not <c>%LOCALAPPDATA%\Versio\logs</c>: that folder is inside the per-user Velopack
    /// install, and uninstall of Setup.exe deletes it. See
    /// <see cref="Versio.Infrastructure.FileSystem.AppUserPaths"/>.
    /// </para>
    /// </remarks>
    public static string DefaultDirectory => AppUserPaths.LogsDirectory;

    /// <param name="directory">Folder to write into. Defaults to
    /// <see cref="DefaultDirectory"/>; tests pass a temp folder.</param>
    /// <param name="retainDays">Files last written before this many days ago are deleted.</param>
    public RollingFileSink(string? directory = null, int retainDays = DefaultRetainDays)
    {
        if (directory is null)
        {
            AppUserPaths.MigrateFromLegacyInstallDirectory();
        }

        _directory = directory ?? DefaultDirectory;
        _retainDays = retainDays;
        Directory.CreateDirectory(_directory);
        Prune();
    }

    /// <summary>
    /// Where the files are being written.
    /// </summary>
    /// <remarks>
    /// Exposed so the app can log the path at startup and open it from the settings menu. A
    /// sink cannot log about itself without recursing through the logger that owns it, and
    /// "where is the log file" should never require knowing the layout of <c>%LOCALAPPDATA%</c>.
    /// </remarks>
    public string LogDirectory => _directory;

    /// <summary>Full path of the file today's entries are going to.</summary>
    /// <remarks>
    /// Opens it if nothing has been written yet, because the name is not known until then:
    /// another Versio holding today's file sends this one to its own. Read at startup to log
    /// where the log is, so it has to name the file that is really being written.
    /// </remarks>
    public string CurrentFile
    {
        get
        {
            lock (_gate)
            {
                // Named, never reopened, once disposed: a property read that quietly opened a
                // file handle after Dispose would leak it for the rest of the process.
                if (_disposed)
                {
                    return _openPath.Length > 0
                        ? _openPath
                        : Path.Combine(_directory, FileNameFor(Today()));
                }

                EnsureOpen(Today());
                return _openPath;
            }
        }
    }

    public void Emit(LogEvent entry)
    {
        lock (_gate)
        {
            // Same guard as CurrentFile, for the same reason: after Dispose, EnsureOpen
            // would quietly reopen the file through a FileStream nobody will ever close.
            // That is exactly what happened on every clean exit until 2026-10-14 — the
            // sink's Exit handler was registered before the shell's, so the shutdown
            // lines (watcher stop, "released this repository") arrived here disposed,
            // reached disk only through that leaked reopen, and "Dispose closes the
            // file" was a sentence the class said and did not mean. The handler order
            // is fixed in App.xaml.cs; a line arriving here anyway is a caller writing
            // after shutdown, and the ring-buffer sink still has it.
            if (_disposed)
            {
                return;
            }

            // The LOCAL day, matching what Format() renders. A file named for the 13th
            // holding entries stamped 17:00 on the 12th is the confusion this whole change
            // was about, and the file name is what somebody picks when asked for "yesterday's
            // log".
            var day = DateOnly.FromDateTime(entry.Timestamp.ToLocalTime().DateTime);
            EnsureOpen(day);
            _writer!.WriteLine(entry.Format());
        }
    }

    /// <summary>Have a writer open on <paramref name="day"/>'s file. Called under the gate.</summary>
    private void EnsureOpen(DateOnly day)
    {
        if (_writer is not null && day == _openFor)
        {
            return;
        }

        _writer?.Dispose();
        _writer = null;

        (_writer, _openPath) = OpenFor(day);

        var rolled = _openFor;
        _openFor = day;

        // Prune on the roll, not only at construction. Versio is a desktop app that
        // gets left open for days at a time; pruning once at startup would let the
        // folder grow for as long as the session lasts, which is precisely the
        // machine where nobody is restarting anything.
        if (rolled != default)
        {
            Prune();
        }
    }

    /// <summary>
    /// The day's shared file, or this process's own beside it when another Versio holds it.
    /// </summary>
    /// <remarks>
    /// Shared for reading, so the file can be opened in an editor — or tailed — while Versio
    /// writes it. Not shared for writing: that is what sends a second process to its own
    /// file, see the class remarks. A failure on the fallback too is left to throw; the
    /// logger reports a dead sink once, which is the right answer when neither file can be
    /// written.
    /// </remarks>
    private (StreamWriter Writer, string Path) OpenFor(DateOnly day)
    {
        var shared = Path.Combine(_directory, FileNameFor(day));
        try
        {
            return (Open(shared), shared);
        }
        catch (IOException) when (File.Exists(shared))
        {
            var own = Path.Combine(
                _directory, $"versio-{day:yyyy-MM-dd}-{Environment.ProcessId}.log");
            return (Open(own), own);
        }
    }

    private static StreamWriter Open(string path) =>
        new(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

    /// <summary>Delete files older than the retention window.</summary>
    /// <remarks>
    /// Judged by last-write time rather than by parsing the date out of the name: a file
    /// with an unexpected name is then simply left alone instead of being deleted on the
    /// strength of a failed parse.
    /// </remarks>
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
                // In use, or being read. It will be pruned on the next roll or launch.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: not worth failing a launch over a file we cannot remove.
            }
        }
    }

    /// <summary>Today where the user is, so <see cref="CurrentFile"/> names the file that
    /// entries are actually going to.</summary>
    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Sortable by name, which is what makes the folder readable at a glance.</summary>
    private static string FileNameFor(DateOnly day) => $"versio-{day:yyyy-MM-dd}.log";

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _disposed = true;
        }
    }
}
