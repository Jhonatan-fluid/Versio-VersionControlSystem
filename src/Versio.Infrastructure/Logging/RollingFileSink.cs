using System.Text;
using Versio.Core.Logging;

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

    /// <summary>
    /// Where logs live for the current user: <c>%LOCALAPPDATA%\Versio\logs</c>.
    /// </summary>
    /// <remarks>
    /// <b>Local, deliberately, not Roaming.</b> On a domain with roaming profiles everything
    /// under <c>%APPDATA%</c> is copied to the server at logoff and back at logon. Diagnostic
    /// logs are machine-specific by definition, so roaming them would slow every sign-in to
    /// carry data that is meaningless on the other machine — and this audience is far more
    /// likely than most to be on exactly that kind of managed domain.
    /// </remarks>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Versio",
        "logs");

    /// <param name="directory">Folder to write into. Defaults to
    /// <see cref="DefaultDirectory"/>; tests pass a temp folder.</param>
    /// <param name="retainDays">Files last written before this many days ago are deleted.</param>
    public RollingFileSink(string? directory = null, int retainDays = DefaultRetainDays)
    {
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
    public string CurrentFile => Path.Combine(_directory, FileNameFor(Today()));

    public void Emit(LogEvent entry)
    {
        lock (_gate)
        {
            // The LOCAL day, matching what Format() renders. A file named for the 13th
            // holding entries stamped 17:00 on the 12th is the confusion this whole change
            // was about, and the file name is what somebody picks when asked for "yesterday's
            // log".
            var day = DateOnly.FromDateTime(entry.Timestamp.ToLocalTime().DateTime);
            if (_writer is null || day != _openFor)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(
                    Path.Combine(_directory, FileNameFor(day)),
                    append: true,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };

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

            _writer.WriteLine(entry.Format());
        }
    }

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
        }
    }
}
