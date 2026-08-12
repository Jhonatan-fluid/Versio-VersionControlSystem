using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Versio.App.Theme;
using Versio.App.ViewModels;
using Versio.Core.Git;

namespace Versio.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        _shell = shell;
        DataContext = shell;

        // Auto-scroll on append, unless paused. Done here rather than in the view model
        // because a ScrollViewer is a view concern.
        shell.Log.RowsAppended += () =>
        {
            if (!shell.Log.IsPaused && shell.Log.Rows.Count > 0)
            {
                LogList.ScrollIntoView(shell.Log.Rows[^1]);
            }
        };

        shell.Log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogViewModel.IsExpanded))
            {
                ApplyLogVisibility();
            }
        };

        // A rejected folder needs a modal the user cannot miss. The view model raises an
        // event rather than showing one itself, so opening a repository stays testable.
        shell.RepositoryRejected += ShowRepositoryRejected;

        ApplyLogVisibility();
        SyncThemeRadios();

        InputBindings.Add(new KeyBinding(shell.RefreshCommand, Key.F5, ModifierKeys.None));
    }

    /// <summary>
    /// Explain why a folder cannot be opened, and offer to choose another.
    /// </summary>
    /// <remarks>
    /// Versio runs entirely on Git, so this is a normal outcome rather than an error path.
    /// The message says what is wrong and what to do about it, and accepting reopens the
    /// picker — the user is never left staring at an app that silently does nothing.
    /// </remarks>
    private async void ShowRepositoryRejected(RepositoryLocation location)
    {
        var caption = location.Kind switch
        {
            RepositoryKind.Bare => "Bare repository",
            RepositoryKind.Inaccessible => "Folder unavailable",
            _ => "Not a Git repository",
        };

        var body = $"{location.RequestedPath}\n\n{location.Explanation}\n\n" +
                   "Choose a different folder?";

        var answer = MessageBox.Show(this, body, caption,
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.OK);

        if (answer == MessageBoxResult.OK)
        {
            await PickRepositoryAsync();
        }
    }

    /// <summary>
    /// Collapse the log list, leaving its header in place.
    /// </summary>
    /// <remarks>
    /// The header stays visible so the panel can be reopened and the entry count is still
    /// readable — collapsing the whole thing to nothing leaves no obvious way back.
    /// </remarks>
    private void ApplyLogVisibility()
    {
        LogList.Visibility = _shell.Log.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        LogChevron.Text = (string)FindResource(
            _shell.Log.IsExpanded ? "Icon.ChevronDown" : "Icon.ChevronUp");
    }

    private async void OnOpenRepository(object sender, RoutedEventArgs e) =>
        await PickRepositoryAsync();

    /// <summary>Show the folder picker and hand the result to the view model.</summary>
    /// <remarks>
    /// The dialog lives in the view because a dialog is a view concern, and keeping it here
    /// is what lets <c>ShellViewModel.OpenAsync</c> be driven straight from a test.
    /// </remarks>
    private async Task PickRepositoryAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Open a Git repository",
            Multiselect = false,
            InitialDirectory = _shell.RepositoryPath ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _shell.OpenAsync(dialog.FolderName);
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        SyncThemeRadios();
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } &&
            Enum.TryParse<AppTheme>(tag, out var theme))
        {
            ThemeManager.Apply(theme);
        }
    }

    /// <summary>Reflect the active theme without re-triggering Checked handlers.</summary>
    private void SyncThemeRadios()
    {
        ThemeSystem.Checked -= OnThemeChanged;
        ThemeLight.Checked -= OnThemeChanged;
        ThemeDark.Checked -= OnThemeChanged;

        ThemeSystem.IsChecked = ThemeManager.Current == AppTheme.System;
        ThemeLight.IsChecked = ThemeManager.Current == AppTheme.Light;
        ThemeDark.IsChecked = ThemeManager.Current == AppTheme.Dark;

        ThemeSystem.Checked += OnThemeChanged;
        ThemeLight.Checked += OnThemeChanged;
        ThemeDark.Checked += OnThemeChanged;
    }
}
