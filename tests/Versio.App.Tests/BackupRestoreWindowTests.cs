using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Core.Sync;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The dialog that offers the copies taken before an import.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new window ships with a test that constructs it against the real stylesheet.</b> Not
/// optional here: the compiler resolves no <c>StaticResource</c>, so a style that does not fit
/// the element asking for it is invisible until something builds the window — and twice a
/// window has been added without one of these and twice it threw in front of somebody.
/// </para>
/// <para>
/// The behaviour worth asserting beyond that is the refusal: <c>Chosen</c> must be null unless
/// somebody picked a row and pressed Restore. Everything downstream treats null as cancel, and
/// a dialog that returned a row it had merely highlighted would restore a project nobody chose.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class BackupRestoreWindowTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static ProjectBackup Backup(string stamp, DateTimeOffset? taken) => new()
    {
        Path = $".versio/local/backups/b1/{stamp}-Machine.project",
        TakenUtc = taken,
        ProjectFileName = "Machine.project",
    };

    private static BackupRestoreWindow Dialog(params ProjectBackup[] backups) =>
        new("Machine", backups.Length > 0
            ? backups
            : [Backup("20260814-093000", Now.AddHours(-3))], Now);

    [Fact]
    public void The_dialog_builds_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            var window = Dialog();

            Assert.Equal("Restore a saved copy", window.Title);

            // Nothing is chosen until somebody chooses. A dialog that opens with an answer
            // already in it is a dialog that has decided for the user.
            Assert.Null(window.Chosen);
        });
    }

    [Fact]
    public void It_says_how_many_copies_there_are_and_names_the_project()
    {
        StaHarness.Run(() =>
        {
            var one = Dialog();
            Assert.Contains("one saved copy of Machine", Headline(one));

            var several = Dialog(
                Backup("20260814-093000", Now.AddHours(-3)),
                Backup("20260813-120000", Now.AddDays(-1)));

            Assert.Contains("2 saved copies of Machine", Headline(several));
        });
    }

    [Fact]
    public void Restore_is_dead_until_a_copy_is_picked()
    {
        StaHarness.Run(() =>
        {
            var window = Dialog();
            var list = Control<ListBox>(window, "BackupList");
            var restore = Control<Button>(window, "RestoreButton");

            Assert.False(restore.IsEnabled);
            Assert.Contains("Choose a copy", Control<TextBlock>(window, "SummaryText").Text);

            list.SelectedIndex = 0;

            Assert.True(restore.IsEnabled);

            // And the summary says what will happen, in the same words the row showed.
            Assert.Contains("Machine.project", Control<TextBlock>(window, "SummaryText").Text);
        });
    }

    [Fact]
    public void Picking_and_pressing_restore_returns_that_copy()
    {
        StaHarness.Run(() =>
        {
            var older = Backup("20260813-120000", Now.AddDays(-1));
            var window = Dialog(Backup("20260814-093000", Now.AddHours(-3)), older);

            var list = Control<ListBox>(window, "BackupList");

            // Index 1 is the older one: the caller hands them over newest first.
            list.SelectedIndex = 1;
            Control<Button>(window, "RestoreButton").RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(older.Path, window.Chosen?.Path);
        });
    }

    [Fact]
    public void Highlighting_without_pressing_restore_chooses_nothing()
    {
        // The whole safety of this dialog: everything downstream treats null as cancel.
        StaHarness.Run(() =>
        {
            var window = Dialog();
            Control<ListBox>(window, "BackupList").SelectedIndex = 0;

            Assert.Null(window.Chosen);
        });
    }

    [Fact]
    public void A_copy_with_no_readable_time_is_listed_and_says_so()
    {
        // A file somebody dropped in by hand, or one from a future version. It is very possibly
        // the thing they are looking for, and a fabricated time would be worse than none.
        StaHarness.Run(() =>
        {
            var window = Dialog(Backup("before-the-upgrade", taken: null));
            var rows = Assert.IsAssignableFrom<IEnumerable<BackupRow>>(
                Control<ListBox>(window, "BackupList").ItemsSource).ToList();

            Assert.Single(rows);
            Assert.Equal("Machine.project", rows[0].When);
            Assert.Contains("no time on it", rows[0].Detail);
        });
    }

    [Fact]
    public void A_time_is_shown_in_the_reader_s_own_clock()
    {
        // Stored UTC because a filename has to sort; shown local because that is the clock
        // somebody was looking at when they made the change they are trying to get back.
        StaHarness.Run(() =>
        {
            var taken = Now.AddHours(-3);
            var window = Dialog(Backup("20260814-090000", taken));

            var rows = Assert.IsAssignableFrom<IEnumerable<BackupRow>>(
                Control<ListBox>(window, "BackupList").ItemsSource).ToList();

            Assert.Contains(taken.ToLocalTime().ToString("HH:mm"), rows[0].When);
            Assert.Contains("3 hour(s) ago", rows[0].Detail);
        });
    }

    [Fact]
    public void The_main_window_wires_the_dialog_to_the_panel()
    {
        // THE LINK NOTHING ELSE COVERS. Every flow test sets AskWhichBackup itself, so all of
        // them stay green with the real wiring deleted — which is exactly how four features in
        // this codebase shipped with no reachable caller. MainWindow's constructor is where the
        // hooks are attached, so building one and reading the hook is a real reachability test.
        StaHarness.Run(() =>
        {
            var sink = new InMemoryLogSink();
            var logger = new VersioLogger(new SystemClock()).AddSink(sink);
            var shell = new ShellViewModel(
                logger, new LogViewModel(sink), toolchains: (_, _) => null);

            _ = new MainWindow(shell);

            Assert.NotNull(shell.Sync.AskWhichBackup);

            // And its neighbour, which had the same shape and the same risk.
            Assert.NotNull(shell.Sync.ConfirmImport);
        });
    }

    private static string Headline(BackupRestoreWindow window) =>
        Control<TextBlock>(window, "Headline").Text;

    private static T Control<T>(BackupRestoreWindow window, string name)
        where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));
}
