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

    /// <summary>A section header.</summary>
    public static SystemInfoRow Section(string label) => new(label, string.Empty, IsHeader: true);
}

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
    private readonly IReadOnlyList<SystemInfoRow> _rows;

    public SystemInfoWindow(IReadOnlyList<SystemInfoRow> rows)
    {
        _rows = rows;
        InitializeComponent();
        Rows.ItemsSource = rows;
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
