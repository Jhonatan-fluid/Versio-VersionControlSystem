using System.Globalization;
using System.Windows;
using Versio.Core.Sync;

namespace Versio.App.Views;

/// <summary>One saved copy, as the list shows it.</summary>
/// <remarks>
/// A view row rather than the <see cref="ProjectBackup"/> itself, because the two strings on
/// screen are formatting decisions — local time, and a relative phrase — and Core holds no
/// opinion about either. It carries the backup so the caller gets the record back rather than
/// having to match on a display string, which is the mistake the branch-delete dialog made
/// once with <c>Where</c>.
/// </remarks>
public sealed record BackupRow
{
    public required ProjectBackup Backup { get; init; }

    /// <summary>The line somebody navigates by.</summary>
    public required string When { get; init; }

    /// <summary>The file name, and how long ago.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Choose one of the copies taken before an import.
/// </summary>
/// <remarks>
/// <para>
/// Safeguards §5.E. Ten copies per binding have been written before every import since the
/// import path existed and nothing could reach them — so after the one operation they exist to
/// undo, the engineer had an unusable project and no route to the file that would fix it.
/// </para>
/// <para>
/// <b>Times are shown in local time.</b> They are stored UTC, which is right for a filename
/// that has to sort, and wrong for a person deciding which copy is "the one from before lunch".
/// </para>
/// </remarks>
public partial class BackupRestoreWindow : Window
{
    private readonly IReadOnlyList<BackupRow> _rows;

    public BackupRestoreWindow(
        string projectName, IReadOnlyList<ProjectBackup> backups, DateTimeOffset now)
    {
        InitializeComponent();

        Headline.Text = backups.Count == 1
            ? $"There is one saved copy of {projectName}."
            : $"There are {backups.Count} saved copies of {projectName}. The newest is first.";

        _rows = [.. backups.Select(b => Row(b, now))];
        BackupList.ItemsSource = _rows;

        // Nothing preselected, deliberately. The newest copy is the likeliest answer and it is
        // also the one most likely to be the damage somebody is trying to undo — so this is a
        // decision the dialog must not appear to have made for them.
        BackupList.SelectionChanged += (_, _) => Describe();
        RestoreButton.Click += (_, _) => Accept();

        Describe();
    }

    /// <summary>The copy the user chose, or null if they cancelled.</summary>
    public ProjectBackup? Chosen { get; private set; }

    private static BackupRow Row(ProjectBackup backup, DateTimeOffset now) => new()
    {
        Backup = backup,

        When = backup.TakenUtc is { } taken
            ? taken.ToLocalTime().ToString("dddd d MMMM, HH:mm", CultureInfo.CurrentCulture)

            // A file whose name this could not read. Named for what it is rather than dressed
            // up with a fabricated time — the file name is the only honest thing to lead with.
            : backup.ProjectFileName,

        Detail = backup.TakenUtc is { } when
            ? $"{backup.ProjectFileName} · {Ago(now - when)}"
            : "Versio did not write this copy, so there is no time on it",
    };

    /// <summary>"3 hours ago", for the reader who is counting backwards from now.</summary>
    private static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} minute(s) ago",
        { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} hour(s) ago",
        _ => $"{(int)elapsed.TotalDays} day(s) ago",
    };

    private void Describe()
    {
        var chosen = BackupList.SelectedItem as BackupRow;

        RestoreButton.IsEnabled = chosen is not null;

        SummaryText.Text = chosen is null
            ? "Choose a copy to restore."
            : $"The project will be replaced with {chosen.Backup.ProjectFileName} " +
              $"as it was on {chosen.When}.";
    }

    private void Accept()
    {
        if (BackupList.SelectedItem is not BackupRow row)
        {
            return;
        }

        // Set first, and closing is how it gets delivered rather than what it is — the same
        // shape ClonePromptWindow.Finish uses, and for the same reason.
        Chosen = row.Backup;

        // DialogResult exists only for a window shown with ShowDialog and throws on any other.
        // The guard is what lets this whole flow be driven by a test with nothing on screen.
        if (IsLoaded)
        {
            DialogResult = true;
        }
    }
}
