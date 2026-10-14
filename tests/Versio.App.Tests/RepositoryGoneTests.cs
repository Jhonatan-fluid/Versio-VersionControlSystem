using System.Diagnostics;
using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A repository folder that vanishes mid-session is said once, not once per poll.
/// </summary>
/// <remarks>
/// <para>
/// Measured live on 2026-10-14: a repository folder renamed while Versio had it open made
/// every git call throw <c>Win32Exception</c> ("The directory name is invalid") out of
/// <c>Process.Start</c>, and the status poll retried every 20 s — so the generic handler
/// raised an identical "Something went wrong" toast on every tick for as long as the window
/// stayed open. A storm of identical cards is how the one that matters goes unread.
/// </para>
/// <para>
/// Against a <b>real git repository really moved on disk</b>, because the claim under test
/// is partly Windows': that a directory tree can be renamed while the shell's watchers hold
/// handles inside it — which is exactly how the live incident happened.
/// </para>
/// <para>
/// This is detection and wording only. SAFEGUARDS §5.H — classifying a vanished repository
/// against the circuit breaker mid-run — stays deferred on purpose; these tests pin the
/// narrower promise that the notice appears once per disappearance and is news again after
/// the folder has been back.
/// </para>
/// </remarks>
public sealed class RepositoryGoneTests : IDisposable
{
    private const string GoneHeadline = "The repository folder is gone.";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "versio-gone-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Where the repository was moved to, so Dispose can find it wherever it is.</summary>
    private string? _movedTo;

    public RepositoryGoneTests()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        Directory.CreateDirectory(_path);
        Git("init -q -b main .");
        Git("config user.email test@versio");
        Git("config user.name test");
        File.WriteAllText(Path.Combine(_path, "machine.st"), "PROGRAM Main\nEND_PROGRAM\n");
        Git("add .");
        Git("commit -qm first");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _movedTo, _path })
        {
            try
            {
                if (path is not null && Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or DirectoryNotFoundException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    [SkippableFact]
    public async Task A_vanished_repository_is_said_once_and_is_news_again_after_it_returns()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = Build();
        await shell.OpenAsync(_path);
        await shell.RefreshAsync();
        Assert.Equal(0, GoneCards(shell));

        // The rename the live incident was: the folder moves, the watchers' handles move
        // with it, and the shell goes on polling the old path.
        _movedTo = _path + "-moved";
        Directory.Move(_path, _movedTo);

        await shell.RefreshAsync();
        await shell.RefreshAsync();
        await shell.RefreshAsync();

        // Said once for three failing polls — the storm was one toast per poll. The
        // notification mirror in the log is the stable count: an identical headline
        // refreshes the card rather than stacking a second, so the card count alone
        // cannot tell one report from three.
        Assert.Equal(1, GoneCards(shell));
        Assert.Equal(1, TimesSaid());

        // Back, and a refresh works again...
        Directory.Move(_movedTo, _path);
        _movedTo = null;
        await shell.RefreshAsync();

        // ...so a second disappearance is a new report, not a suppressed repeat —
        // refreshed onto the same card, said a second time in the log.
        _movedTo = _path + "-moved";
        Directory.Move(_path, _movedTo);
        await shell.RefreshAsync();

        Assert.Equal(1, GoneCards(shell));
        Assert.Equal(2, TimesSaid());
    }

    private static int GoneCards(ShellViewModel shell) =>
        shell.Notifications.Visible.Count(
            n => string.Equals(n.Headline, GoneHeadline, StringComparison.Ordinal));

    /// <summary>How often the notice was posted: its Error-level mirror in the log,
    /// one line per Post, where an updated card leaves no second trace.</summary>
    private int TimesSaid() =>
        _sink.Snapshot().Count(
            e => e.Level == Versio.Core.Logging.LogLevel.Error
                 && e.Message.StartsWith(GoneHeadline, StringComparison.Ordinal));

    private InMemoryLogSink _sink = null!;

    private ShellViewModel Build()
    {
        _sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(_sink);

        // No toolchain: a temp repository with no PLC project needs none, and a machine
        // without one is a state the app already handles.
        return new ShellViewModel(logger, new LogViewModel(_sink), toolchains: (_, _) => null);
    }

    private string Git(string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        var stdout = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return stdout;
    }

    private static bool GitIsAvailable()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            git!.WaitForExit();
            return git.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
