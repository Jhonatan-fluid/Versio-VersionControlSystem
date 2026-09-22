using System.IO;
using System.Text.Json;
using Versio.App.ViewModels;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// One repository, one Versio.
/// </summary>
/// <remarks>
/// <para>
/// Safeguards §5.G. Two instances on one repository means two orchestrators writing
/// <c>repo.json</c> and <c>state.json</c> over each other, and nothing stopped it: the only
/// signal was <c>LockRecovery.HeldByLiveSession</c>, which promised <i>"opening read-only"</i>
/// that nothing implemented — and which only fired for a binding that happened to hold a lock,
/// so an idle repository opened twice with no complaint at all.
/// </para>
/// <para>
/// <b>The rules are asserted without a window in <c>RepositoryPresenceTests</c>.</b> What is
/// here is that the shell consults them, writes a marker when it opens, and gives it back —
/// the half that would otherwise be a policy with no caller.
/// </para>
/// </remarks>
public sealed class SecondInstanceTests
{
    private static ShellViewModel Shell(IProcessProbe? processes = null) =>
        TestShell.Build(processes);

    /// <summary>
    /// A probe that says the other session is alive.
    /// </summary>
    /// <remarks>
    /// The real one requires the process NAME to start with "Versio", so under a test host no
    /// pid on the machine can ever look like a live instance — and the refusal is the branch
    /// the whole guard exists for. Everything else here uses the real probe.
    /// </remarks>
    private sealed class Running : IProcessProbe
    {
        public bool IsVersioProcessAlive(int processId, string sessionId) => true;

        public bool IsProcessAlive(int processId) => true;
    }

    private static string MarkerPath(TempRepo repo) =>
        Path.Combine(
            repo.Path,
            RepositoryPresence.MarkerPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A marker as though another Versio had written one.</summary>
    private static void GivenAMarker(TempRepo repo, SessionMarker marker)
    {
        var path = MarkerPath(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(marker));
    }

    private static SessionMarker Marker(string machine, int pid, DateTimeOffset boot) => new()
    {
        Machine = machine,
        User = "someone",
        ProcessId = pid,
        SessionId = $"{pid}-{boot.ToUnixTimeSeconds()}",
        BootId = boot,
        OpenedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Opening_a_repository_claims_it()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.True(File.Exists(MarkerPath(repo)),
            "opening a repository wrote no presence marker");

        var written = JsonSerializer.Deserialize<SessionMarker>(
            File.ReadAllText(MarkerPath(repo)));

        Assert.Equal(Environment.ProcessId, written?.ProcessId);
        Assert.Equal(Environment.MachineName, written?.Machine);
    }

    [Fact]
    public async Task Closing_gives_the_repository_back()
    {
        // Otherwise the next launch has to work out that this process is gone before it can
        // open its own repository — which works, but leaves a marker lying around claiming a
        // pid that Windows will hand to something else.
        using var repo = new TempRepo();

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        Assert.True(File.Exists(MarkerPath(repo)));

        shell.Dispose();

        Assert.False(File.Exists(MarkerPath(repo)));
    }

    [Fact]
    public async Task A_repository_another_live_Versio_has_open_is_refused()
    {
        // THIS machine, THIS boot, a pid that is not ours, and a probe that says it is alive.
        // Not our own pid: that is the "this very window" case, which is correctly allowed.
        using var repo = new TempRepo();
        GivenAMarker(repo, Marker(
            Environment.MachineName, Environment.ProcessId + 1, BootTime()));

        using var shell = Shell(new Running());

        var told = string.Empty;
        shell.RepositoryBusyElsewhere += why => told = why;

        await shell.OpenAsync(repo.Path);

        Assert.Contains("already open in another Versio", told);
        Assert.Null(shell.RepositoryPath);
    }

    // ------------------------------------------------------------ the hand-over at launch
    //
    // Seen on 2026-09-21: clicking Versio in the Start menu while it was running drew a whole
    // empty shell with a modal naming the other window's pid. A launch has nothing else to
    // show, so it brings that window forward and goes.

    [Fact]
    public async Task A_launch_whose_repository_is_open_elsewhere_hands_over_to_that_window()
    {
        using var repo = new TempRepo();
        var holder = Environment.ProcessId + 1;
        GivenAMarker(repo, Marker(Environment.MachineName, holder, BootTime()));

        using var shell = Shell(new Running());
        int? broughtForward = null;
        shell.BringWindowForward = pid =>
        {
            broughtForward = pid;
            return true;
        };

        var handedOver = false;
        var told = false;
        shell.HandedOverToOtherWindow += () => handedOver = true;
        shell.RepositoryBusyElsewhere += _ => told = true;

        await shell.OpenAtStartupAsync(repo.Path);

        Assert.Equal(holder, broughtForward);
        Assert.True(handedOver, "the window was not told to close");
        Assert.False(told, "a dialog was raised over a window that is about to close");
        Assert.Null(shell.RepositoryPath);
    }

    [Fact]
    public void The_window_that_handed_over_closes_itself()
    {
        // The window's half. The shell raising the event proves nothing if MainWindow stopped
        // listening: it would sit there empty, which is the state the hand-over removes.
        StaHarness.Run(async () =>
        {
            using var repo = new TempRepo();
            GivenAMarker(repo, Marker(
                Environment.MachineName, Environment.ProcessId + 1, BootTime()));

            using var shell = Shell(new Running());
            shell.BringWindowForward = _ => true;

            var window = new Versio.App.Views.MainWindow(shell)
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
                Width = 400,
                Height = 300,
            };
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();

            await shell.OpenAtStartupAsync(repo.Path);

            // Posted at Background by the window, then re-posted once by its close guard.
            for (var i = 0; i < 3 && !closed; i++)
            {
                await window.Dispatcher.InvokeAsync(
                    () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            Assert.True(closed, "the window stayed open after handing over");
        });
    }

    [Fact]
    public async Task A_launch_that_cannot_reach_the_other_window_still_explains()
    {
        // The window gone between the check and now, or Windows refusing the focus change:
        // the dialog is the fallback, and the window stays so it has something to sit on.
        using var repo = new TempRepo();
        GivenAMarker(repo, Marker(
            Environment.MachineName, Environment.ProcessId + 1, BootTime()));

        using var shell = Shell(new Running());
        shell.BringWindowForward = _ => false;

        var handedOver = false;
        var told = false;
        shell.HandedOverToOtherWindow += () => handedOver = true;
        shell.RepositoryBusyElsewhere += _ => told = true;

        await shell.OpenAtStartupAsync(repo.Path);

        Assert.False(handedOver);
        Assert.True(told);
    }

    [Fact]
    public async Task Switching_repository_inside_a_running_window_never_closes_it()
    {
        // Only a launch hands over. A window somebody is working in, choosing a repository
        // from its own dropdown, keeps the dialog and stays open.
        using var repo = new TempRepo();
        GivenAMarker(repo, Marker(
            Environment.MachineName, Environment.ProcessId + 1, BootTime()));

        using var shell = Shell(new Running());
        var asked = false;
        shell.BringWindowForward = _ => asked = true;

        var handedOver = false;
        var told = false;
        shell.HandedOverToOtherWindow += () => handedOver = true;
        shell.RepositoryBusyElsewhere += _ => told = true;

        await shell.OpenAsync(repo.Path);

        Assert.False(asked, "a plain open reached for another window");
        Assert.False(handedOver);
        Assert.True(told);
    }

    [Fact]
    public async Task A_marker_left_by_a_dead_session_does_not_stop_anybody()
    {
        // A crash must not lock somebody out of their own repository for ever. Pid 1 exists on
        // Windows only as the idle process, and never as a Versio.
        using var repo = new TempRepo();
        GivenAMarker(repo, Marker(Environment.MachineName, pid: 1, BootTime()));

        using var shell = Shell();

        var refused = false;
        shell.RepositoryBusyElsewhere += _ => refused = true;

        await shell.OpenAsync(repo.Path);

        Assert.False(refused);
        Assert.Equal(repo.Path, shell.RepositoryPath);

        // And the dead session's marker has been replaced by this one's.
        var written = JsonSerializer.Deserialize<SessionMarker>(
            File.ReadAllText(MarkerPath(repo)));

        Assert.Equal(Environment.ProcessId, written?.ProcessId);
    }

    [Fact]
    public async Task A_marker_from_before_the_last_restart_does_not_stop_anybody()
    {
        // The pid may well be alive again and belong to something else entirely.
        using var repo = new TempRepo();
        GivenAMarker(repo, Marker(
            Environment.MachineName, Environment.ProcessId, BootTime().AddDays(-3)));

        using var shell = Shell();

        var refused = false;
        shell.RepositoryBusyElsewhere += _ => refused = true;

        await shell.OpenAsync(repo.Path);

        Assert.False(refused);
        Assert.Equal(repo.Path, shell.RepositoryPath);
    }

    [Fact]
    public async Task A_corrupt_marker_does_not_make_a_repository_unusable()
    {
        // One bad file must not turn into a repository nobody can open. The guard is a
        // courtesy; the repository is the point.
        using var repo = new TempRepo();
        var path = MarkerPath(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        Assert.Equal(repo.Path, shell.RepositoryPath);
    }

    [Fact]
    public async Task The_same_window_reopening_its_own_repository_is_not_a_conflict()
    {
        // The most ordinary action there is, and it goes through the same claim path.
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        var refused = false;
        shell.RepositoryBusyElsewhere += _ => refused = true;

        await shell.OpenAsync(repo.Path);

        Assert.False(refused);
        Assert.Equal(repo.Path, shell.RepositoryPath);
    }

    [Fact]
    public async Task Switching_away_releases_the_first_repository()
    {
        // Switching A -> B -> A must not find A still claimed by this same window.
        using var first = new TempRepo();
        using var second = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(first.Path);
        await shell.OpenAsync(second.Path);

        Assert.False(File.Exists(MarkerPath(first)), "the first repository is still claimed");
        Assert.True(File.Exists(MarkerPath(second)));

        var refused = false;
        shell.RepositoryBusyElsewhere += _ => refused = true;

        await shell.OpenAsync(first.Path);

        Assert.False(refused);
        Assert.Equal(first.Path, shell.RepositoryPath);
    }

    /// <summary>This machine's boot time, the way MachineIdentity derives it.</summary>
    private static DateTimeOffset BootTime() =>
        new MachineIdentity().BootId;
}
