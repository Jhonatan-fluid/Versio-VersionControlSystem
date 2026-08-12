using System.Windows;
using System.Windows.Controls;
using Versio.App.Theme;
using Versio.App.ViewModels;

namespace Versio.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        _shell = shell;
        DataContext = shell;

        // Auto-scroll on append, unless the user paused it. Scrolling the log is done here
        // rather than in the view model because it is a view concern - the view model has
        // no business knowing a ScrollViewer exists.
        shell.Log.RowsAppended += () =>
        {
            if (!shell.Log.IsPaused && shell.Log.Rows.Count > 0)
            {
                LogList.ScrollIntoView(shell.Log.Rows[^1]);
            }
        };
    }

    private async void OnOpenRepository(object sender, RoutedEventArgs e)
    {
        // The folder picker lives in the view: a dialog is a view concern, and keeping it
        // here is what lets ShellViewModel.OpenAsync be driven straight from a test.
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Open a repository",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _shell.OpenAsync(dialog.FolderName);
        }
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) =>
        ThemeManager.Apply(ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
}
