using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Versio.App.Mvvm;
using Versio.App.Theme;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Settings;

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

        // Discarding a file destroys work git cannot give back, so it is asked about here
        // rather than assumed. Unset would mean "no", which is the right way round for a
        // wiring mistake to fail.
        shell.ConfirmDestructive = (caption, body) =>
            MessageBox.Show(this, body, caption, MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning, MessageBoxResult.Cancel)
            == MessageBoxResult.OK;

        // The guard's questions have three answers, not two, so they need a real dialog for
        // the same reason the branch prompt does. Owned, so it centres on the window and
        // cannot be lost behind it — which for a modal blocking a close would look like a
        // hang rather than a question.
        shell.AskGuard = prompt =>
        {
            var window = new GuardPromptWindow(prompt) { Owner = this };
            window.ShowDialog();
            return window.ChoiceResult;
        };

        // Three named courses of action, so the same window the guard uses rather than a
        // MessageBox: "Yes" is not an answer to "restore, leave or discard".
        shell.AskAboutStashedWork = prompt =>
        {
            var window = new GuardPromptWindow(
                prompt.Title,
                prompt.Body,
                [.. prompt.Choices.Select(c => new PromptOption(AutoStashNarrative.Label(c), c))],
                cancels: StashedWorkChoice.Leave)
            {
                Owner = this,
            };

            window.ShowDialog();
            return window.Result as StashedWorkChoice?;
        };

        // A branch name needs a text box and live validation, which a MessageBox cannot do.
        // The dialog is owned so it centres on the window and cannot be lost behind it.
        shell.PromptForBranchName = () =>
        {
            var prompt = new BranchPromptWindow { Owner = this };
            return prompt.ShowDialog() == true ? prompt.BranchNameResult : null;
        };

        // The clone dialog runs the conversation — verify, then clone — using git handed to
        // it by the shell. The view supplies the window; the view model supplies the
        // operations, so no dialog here ever constructs a git client of its own.
        shell.PromptForClone = initialUrl =>
        {
            var prompt = new ClonePromptWindow(
                shell.VerifyRemoteAsync,
                shell.CloneAsync,
                initialFolder: Path.GetDirectoryName(shell.RepositoryPath),
                initialUrl: initialUrl)
            {
                Owner = this,
            };

            return Task.FromResult(prompt.ShowDialog() == true ? prompt.ClonedPath : null);
        };

        // Deleting branches needs a list with checkboxes and three answers, which no
        // MessageBox can do. The dialog collects the choice; every rule about whether it may
        // happen - the guard, the remote confirmation, the checkout off a branch being
        // deleted - stays in the view model where the repository is.
        shell.PromptForBranchDeletion = candidates =>
        {
            var window = new BranchDeleteWindow(candidates) { Owner = this };
            return window.ShowDialog() == true ? window.Result : null;
        };

        // A repository the user was told they could come back to, which is not there any
        // more. Three answers with real labels, so the same window the guard uses - a
        // MessageBox would have to squeeze "clone it again" into Yes.
        shell.AskAboutMissingRepository = prompt =>
        {
            var window = new GuardPromptWindow(
                prompt.Title,
                prompt.Body,
                [.. prompt.Choices.Select(c => new PromptOption(MissingRepository.Label(c), c))])
            {
                Owner = this,
            };

            window.ShowDialog();
            return window.Result as MissingRepositoryChoice?;
        };

        ApplyLogVisibility();
        SyncThemeRadios();

        InputBindings.Add(new KeyBinding(shell.RefreshCommand, Key.F5, ModifierKeys.None));
    }

    /// <summary>
    /// True once the shell has agreed the window may go.
    /// </summary>
    /// <remarks>
    /// <see cref="OnClosing"/> runs twice for any close that had to wait: once to ask and veto,
    /// and once more when <see cref="Close"/> is called after the answer arrives. Without this
    /// the second pass would ask again, and a user who had chosen "wait" would be asked whether
    /// they wanted to wait for the thing they had just finished waiting for.
    /// </remarks>
    private bool _closeApproved;

    /// <summary>
    /// Do not close while Versio is in the middle of something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until this existed, closing the window killed the process and orphaned whatever CODESYS
    /// it had launched — hidden, since the runner starts it with <c>CREATE_NO_WINDOW</c>, still
    /// holding the project, and part-way through writing a binary file if it was importing.
    /// </para>
    /// <para>
    /// <b>Closing is synchronous and the answer is not.</b> So the close is vetoed
    /// unconditionally, the answer is awaited, and the window is closed again once it arrives.
    /// <c>async void</c> is correct here and only here: an event handler is the one place the
    /// framework gives no task to await, and the body cannot throw — everything inside it is
    /// already guarded.
    /// </para>
    /// <para>
    /// <b>The second close is posted, never called inline.</b> When nothing is running —
    /// overwhelmingly the common case — <see cref="ShellViewModel.RequestCloseAsync"/> completes
    /// without ever yielding, so the <c>await</c> continues on the same stack and
    /// <see cref="Close"/> would run <i>inside</i> this very <c>Closing</c> event. WPF forbids
    /// that outright:
    /// </para>
    /// <code>
    /// InvalidOperationException: Cannot set Visibility to Visible or call Show, ShowDialog,
    /// Close, or WindowInteropHelper.EnsureHandle while a Window is closing.
    /// </code>
    /// <para>
    /// The exception surfaces on the dispatcher rather than here, so the crash handler logs it
    /// and marks it handled — and the window simply stays open. <b>Versio could not be closed
    /// at all</b>, and every unit test passed, because they drive the view model and never the
    /// window. Posting the close lets this event unwind first, which is the whole difference.
    /// </para>
    /// </remarks>
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closeApproved)
        {
            base.OnClosing(e);
            return;
        }

        // Vetoed first, always. The answer may involve waiting 20 seconds for an export, and
        // there is no way to hold the close open while we find out.
        e.Cancel = true;

        if (!await _shell.RequestCloseAsync())
        {
            return;
        }

        _closeApproved = true;

        // Queued behind the event we are still inside. See the remarks.
        await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Normal);
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

    /// <summary>
    /// Put a failure notification's whole message on the clipboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the code-behind rather than on a command, because the clipboard is a view concern
    /// and a view model that reached for <see cref="Clipboard"/> could not be constructed in a
    /// test host without an STA thread — which is a real cost for what is three lines here.
    /// </para>
    /// <para>
    /// <b>The clipboard genuinely fails sometimes.</b> Another process can hold it open, and
    /// <see cref="Clipboard.SetText(string)"/> then throws <see cref="ExternalException"/>.
    /// Letting that reach the dispatcher would take the window down while the user was trying
    /// to copy an error message, which would be a memorable way to lose a bug report.
    /// </para>
    /// </remarks>
    private void OnCopyNotification(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotificationRow row })
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.ClipboardText);
        }
        catch (ExternalException)
        {
            // Another process has the clipboard. Nothing useful to do, and nothing worth
            // interrupting the user over — they can still read the text on the card.
        }
    }

    /// <summary>
    /// Ctrl+Enter commits from the message box.
    /// </summary>
    /// <remarks>
    /// The box accepts newlines, so plain Enter has to keep inserting one - a commit body is
    /// the whole reason the box is multi-line. Ctrl+Enter is the convention every git client
    /// uses for this, and reaching for the mouse after typing a message is the interaction
    /// here that happens dozens of times a day.
    /// </remarks>
    private void OnCommitMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        // Handled either way: a Ctrl+Enter that cannot commit must not fall through and
        // insert a newline the user did not ask for.
        e.Handled = true;

        if (_shell.CommitCommand.CanExecute(null))
        {
            _shell.CommitCommand.Execute(null);
        }
    }

    /// <summary>
    /// Selecting a row in the tree shows whatever it has to show.
    /// </summary>
    /// <remarks>
    /// A code-behind handler because <c>TreeView.SelectedItem</c> is read-only and cannot be
    /// bound - the standard WPF workaround, and cheaper than the attached-behaviour it would
    /// otherwise take.
    /// </remarks>
    private void OnTreeNodeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeRow row)
        {
            _shell.Sync.SelectedNode = row;
        }
    }

    /// <summary>Switch the left panel from the rail.</summary>
    /// <remarks>
    /// Visibility rather than a TabControl: the panels share the card and its header, and a
    /// TabControl would bring its own chrome that would then have to be styled away.
    /// </remarks>
    private void OnRailChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<ShellPanel>(tag, out var panel) ||
            _shell is null)
        {
            return;
        }

        // Set first. The history reads its first page off the back of this, and doing it
        // before the panel is shown means the list is already filling as it appears.
        _shell.ActivePanel = panel;

        ProjectPanel.Visibility = Show(panel == ShellPanel.Project);
        GitPanel.Visibility = Show(panel == ShellPanel.Git);
        HistoryPanel.Visibility = Show(panel == ShellPanel.History);

        // The count badge is about the working tree, which is what the project panel shows.
        ChangeCountBadge.Visibility = Show(panel == ShellPanel.Project);

        static Visibility Show(bool visible) =>
            visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Selecting a row in the history shows it.
    /// </summary>
    /// <remarks>
    /// Code-behind for the same reason the project tree needs it: <c>TreeView.SelectedItem</c>
    /// is read-only and cannot be bound. What was picked - a commit or one file inside it - is
    /// the view model's decision, not this handler's.
    /// </remarks>
    private void OnHistoryRowSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _shell.History.Select(e.NewValue);

    /// <summary>
    /// Show what this machine has: toolchains, versions, paths.
    /// </summary>
    /// <remarks>
    /// The popup closes first. A modal opening from behind an open popup leaves the popup
    /// hanging over it, and the popup closes on its own the moment focus moves - which looks
    /// like a glitch rather than a decision.
    /// </remarks>
    private async void OnOpenSystemInfo(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;

        var rows = await _shell.BuildSystemInfoAsync(CancellationToken.None);
        new SystemInfoWindow(rows) { Owner = this }.ShowDialog();
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
