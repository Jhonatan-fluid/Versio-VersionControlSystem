using System.Text;
using System.Windows;

namespace Versio.App.Views;

/// <summary>One line of the system-information list: a section header, or a label and value.</summary>
/// <remarks>
/// A flat sequence with headers in it rather than a grouped list. The window has one job —
/// being read and copied into a bug report — and a flat list is what pastes usefully.
/// </remarks>
public sealed record SystemInfoRow(string Label, string Value, bool IsHeader = false)
{
    public bool IsValue => !IsHeader;

    /// <summary>
    /// A button under the value, for the one kind of row that is not only read: a vendor's
    /// fix for why its toolchain refused (2026-10-29). Null for every other row.
    /// </summary>
    public SystemInfoAction? Action { get; init; }

    public bool HasAction => Action is not null;

    /// <summary>A section header.</summary>
    public static SystemInfoRow Section(string label) => new(label, string.Empty, IsHeader: true);
}

/// <summary>What a row's button says and does; <see cref="Run"/> returns the sentence to show.</summary>
public sealed record SystemInfoAction(string Label, Func<Task<string>> Run);

/// <summary>
/// What this machine has: toolchains, profiles, versions and paths.
/// </summary>
/// <remarks>
/// <para>
/// Environment, not repository state. All of this used to sit at the top of the project panel,
/// where it was the first thing seen on a tab about changed files, took the room the files
/// needed, and read as though it were describing the open repository. It changes when somebody
/// installs something, not while anybody is working.
/// </para>
/// <para>
/// The rows are gathered by the caller and handed in, so this window knows nothing about
/// toolchains, git or the filesystem — and so the gathering can be tested without a window.
/// </para>
/// </remarks>
public partial class SystemInfoWindow : Window
{
    private IReadOnlyList<SystemInfoRow> _rows;
    private readonly Func<Task<IReadOnlyList<SystemInfoRow>>>? _refresh;

    /// <param name="rows">What to show.</param>
    /// <param name="refresh">How to gather the rows again after a row's action ran, so the
    /// window shows the state the action left rather than the one it was opened on. Null
    /// leaves the rows as they were, which is right for a caller with nothing to act on.</param>
    public SystemInfoWindow(
        IReadOnlyList<SystemInfoRow> rows,
        Func<Task<IReadOnlyList<SystemInfoRow>>>? refresh = null)
    {
        _rows = rows;
        _refresh = refresh;
        InitializeComponent();
        Rows.ItemsSource = rows;
    }

    /// <summary>What the last action said, shown under the list; empty until one has run.</summary>
    internal string LastOutcome => Outcome.Text;

    /// <summary>
    /// A row's button: run the action, say what it said, then gather the rows again.
    /// </summary>
    /// <remarks>
    /// The button is disabled while it runs — the first action takes a UAC prompt and a
    /// helper process, and a second press meanwhile would put up a second prompt. An
    /// exception is shown as the outcome, not thrown out of a click handler where it would
    /// close the window with the message unread.
    /// </remarks>
    internal async Task RunActionAsync(SystemInfoRow row)
    {
        if (row.Action is not { } action)
        {
            return;
        }

        IsEnabled = false;
        try
        {
            Outcome.Text = await action.Run();
            if (_refresh is not null)
            {
                _rows = await _refresh();
                Rows.ItemsSource = _rows;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Outcome.Text = $"The action failed: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnRunAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SystemInfoRow row })
        {
            await RunActionAsync(row);
        }
    }

    /// <summary>Everything on screen, as text worth pasting into a bug report.</summary>
    internal string AsText()
    {
        var text = new StringBuilder();

        foreach (var row in _rows)
        {
            if (row.IsHeader)
            {
                text.AppendLine().AppendLine(row.Label);
                continue;
            }

            text.AppendLine($"  {row.Label,-24} {row.Value}");
        }

        return text.ToString().TrimStart();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AsText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is a shared OS resource and another process can hold it open.
            // Failing to copy a diagnostic is not worth an error dialog on top of whatever
            // the user was already diagnosing.
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
