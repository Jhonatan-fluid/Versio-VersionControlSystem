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

    /// <summary>
    /// The shell, and the panel sizes the user last dragged.
    /// </summary>
    /// <param name="layout">
    /// Read from settings before the window exists, because applying it afterwards would show
    /// a frame at the default size and then jump. Null for a test, which gets the defaults.
    /// </param>
    public MainWindow(ShellViewModel shell, LayoutSettings? layout = null)
    {
        InitializeComponent();
        _shell = shell;
        DataContext = shell;
        _layout = layout ?? new LayoutSettings();

        // The log console's own auto-scroll and chevron went with it into LogPanelView.
        // What is left here is the part a panel cannot own: the row it sits in.
        shell.Log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogViewModel.IsExpanded))
            {
                ApplyLogRowHeight();
            }
        };

        // A rejected folder needs a modal the user cannot miss. The view model raises an
        // event rather than showing one itself, so opening a repository stays testable.
        shell.RepositoryRejected += ShowRepositoryRejected;

        // A repository another Versio already has open. Not a rejection of the folder - it is
        // perfectly good and will open the moment the other window lets go - so it says that
        // rather than anything about the path being wrong.
        //
        // No choices, which GuardPromptWindow renders as a single OK: this is a statement, and
        // a Cancel beside it would imply an alternative that does not exist.
        shell.RepositoryBusyElsewhere += why => Tell("Already open", why);

        // "Open a repository" as a routed command, so a button anywhere in the window - the
        // project panel's empty state is the one that needed it - reaches the folder picker
        // without holding a reference to this window. The picker stays here, where dialogs
        // live. ApplicationCommands.Open carries Ctrl+O, which is also now the shortcut.
        CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Open, async (_, _) => await PickRepositoryAsync()));

        // The same refusal at launch, when the other window could be brought forward: this one
        // has nothing to show and goes. Posted rather than called, because the event is raised
        // from inside the open's busy scope - closing there would ask the close guard about an
        // "Opening…" that is about to end by itself.
        shell.HandedOverToOtherWindow += () =>
            Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);

        // An update is downloaded and the user said Update and restart. The window closes the
        // way the X closes it, through OnClosing and the shell's close guard, so an import in
        // flight is asked about rather than cut short. Posted for the same reason as the line
        // above: the request arrives from inside the update command's own await.
        shell.Updates.RestartRequested += () =>
            Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);

        // The update overlay blocks the mouse by covering the window, but focus can still sit
        // on a panel behind it, where Enter and Ctrl+O would act. So: focus goes onto the
        // overlay when it opens, and PreviewKeyDown drops any key aimed outside it - and any
        // chord inside it too, because a Ctrl+O pressed on the overlay's own Cancel button
        // still bubbles up to the window's shortcuts and opens the folder picker behind it.
        // Tab, Shift+Tab, Enter and Space are what the overlay's buttons need.
        shell.Updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdatesViewModel.IsOverlayOpen) && shell.Updates.IsOverlayOpen)
            {
                Dispatcher.BeginInvoke(FocusUpdateOverlay, System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (shell.Updates.IsOverlayOpen
                && (e.OriginalSource is not DependencyObject target || !UpdateOverlay.IsAncestorOf(target)
                    || (Keyboard.Modifiers & ~ModifierKeys.Shift) != ModifierKeys.None)
                // Alt+F4 still closes, as the title bar's X does: the close guard owns that.
                && !(e.Key == Key.System && e.SystemKey == Key.F4))
            {
                e.Handled = true;
            }
        };

        // Every dialog the application can open, in one class rather than as a run of
        // closures here. Handed to the shell, which passes them down to Sync - so the two
        // view models share one set of questions and this is the only line that wires them.
        //
        // Unset would leave NullPrompts in place, which answers "no" to everything: a
        // wiring mistake destroys nothing rather than silently confirming.
        shell.Prompts = new WindowPrompts(this, shell);

        // After the window has a size, so LogHeight can clamp against it. In the
        // constructor ActualHeight is still zero and a stored 900 px log would be restored
        // whole onto a laptop screen.
        Loaded += (_, _) => ApplyStoredLayout();

        ApplyLogRowHeight();
        SyncThemeRadios();

        InputBindings.Add(new KeyBinding(shell.RefreshCommand, Key.F5, ModifierKeys.None));

        // CTRL+1/2/3 FOR THE RAIL.
        //
        // Switching panels is the most frequent thing anybody does in this window and cost a
        // mouse trip every time. On the WINDOW rather than the rail, because the point is to
        // reach it from wherever you are - reading a diff, typing a commit message, filtering
        // the log - and a binding scoped to the rail would only work once the rail had focus,
        // which is the moment you no longer need a shortcut.
        //
        // Through the rail buttons rather than by setting ActivePanel, because OnRailChanged
        // is what actually shows the panel and tells the history it is being looked at. A
        // shortcut that set the property directly would light the wrong button and leave the
        // panel where it was — and it would be the second thing deciding which panel shows,
        // which is the defect this file's own comments warn about twice.
        Bind(Key.D1, "RailProject");
        Bind(Key.D2, "RailGit");
        Bind(Key.D3, "RailHistory");
        Bind(Key.D4, "RailExplorer");

        // NumPad as well as the number row. A full-size keyboard on a workshop desk is exactly
        // where this application runs, and Ctrl+NumPad1 doing nothing is the kind of small
        // wrongness nobody reports.
        Bind(Key.NumPad1, "RailProject");
        Bind(Key.NumPad2, "RailGit");
        Bind(Key.NumPad3, "RailHistory");
        Bind(Key.NumPad4, "RailExplorer");

        void Bind(Key key, string rail) =>
            InputBindings.Add(new KeyBinding(
                new RelayCommand(() =>
                {
                    if (FindName(rail) is RadioButton button)
                    {
                        button.IsChecked = true;
                    }
                }),
                key,
                ModifierKeys.Control));

        // CTRL+F TO THE ACTIVE PANEL'S FIND BOX (UX-7, second half).
        //
        // On the window for the same reason Ctrl+1/2/3 are: the point is to reach the box
        // from wherever focus is. Each panel exposes FocusFindBox rather than the window
        // grubbing through another control's name scope — the panel knows which box is its
        // find box and whether it is on screen; the window only knows which panel the rail
        // shows. All four rail panels have a box: the explorer's was already there, UX-6
        // added git and history, and a first pass that skipped the explorer on the claim
        // it had none left Ctrl+F dead on the one panel where the box was oldest.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() =>
            {
                if (RailIsChecked("RailProject"))
                {
                    ProjectPanel.FocusFindBox();
                }
                else if (RailIsChecked("RailExplorer"))
                {
                    ExplorerPanel.FocusFindBox();
                }
                else if (RailIsChecked("RailGit"))
                {
                    GitPanel.FocusFindBox();
                }
                else if (RailIsChecked("RailHistory"))
                {
                    HistoryPanel.FocusFindBox();
                }
            }),
            Key.F,
            ModifierKeys.Control));

        bool RailIsChecked(string rail) =>
            FindName(rail) is RadioButton { IsChecked: true };

        // ESCAPE CLOSES THE TWO TRANSIENT SURFACES (UX-7, second half): the settings popup
        // and the ahead/behind preview strip. Both already close on a click outside, so this
        // is the same dismissal for the hand that opened them from the keyboard. The
        // CanExecute gate is the important part — with nothing open the key stays UNHANDLED
        // and falls through to whoever wanted it, instead of the window silently eating
        // every Escape for the rest of the session.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(
                () =>
                {
                    SettingsPopup.IsOpen = false;
                    shell.Remote.DismissPreview();
                },
                () => SettingsPopup.IsOpen || shell.Remote.IsPreviewOpen),
            Key.Escape,
            ModifierKeys.None));
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
    private bool _closeInFlight;

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

        // A second X while the first wait is in flight used to start another
        // RequestCloseAsync. Ignore it; the first answer still owns the close.
        if (_closeInFlight)
        {
            e.Cancel = true;
            return;
        }

        // Vetoed first, always. The answer may involve waiting 20 seconds for an export, and
        // there is no way to hold the close open while we find out.
        e.Cancel = true;
        _closeInFlight = true;

        if (!await _shell.RequestCloseAsync())
        {
            _closeInFlight = false;

            // If the close was for an update, the user has just chosen to keep working. The
            // download is kept and installs when Versio next exits, without restarting it.
            _shell.Updates.RestartDeclined();
            return;
        }

        _closeApproved = true;

        // Written on the way out rather than on every drag. A GridSplitter raises its change
        // continuously, so persisting there would be a file write per pixel of a drag.
        //
        // After the shell has agreed the window may go, so a close somebody cancelled does
        // not record a layout they were only passing through.
        await _shell.RememberLayoutAsync(CurrentLayout());

        // Queued behind the event we are still inside. See the remarks.
        await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Normal);
    }

    /// <summary>
    /// Into the update overlay: its first button when it has one, the overlay itself while
    /// it is restarting and has none - focus left on a panel behind it is a key that acts
    /// on something the user cannot see.
    /// </summary>
    private void FocusUpdateOverlay()
    {
        if (UpdateCancelButton.IsVisible)
        {
            UpdateCancelButton.Focus();
        }
        else if (UpdateTryAgainButton.IsVisible)
        {
            UpdateTryAgainButton.Focus();
        }
        else
        {
            UpdateOverlay.Focus();
        }
    }

    /// <summary>State something that has no question in it.</summary>
    /// <remarks>
    /// No options, which GuardPromptWindow renders as a lone OK. Its own remarks make the
    /// argument: a Cancel beside an acknowledgement implies an alternative there isn't one of.
    /// </remarks>
    private void Tell(string title, string body) =>
        new GuardPromptWindow(title, body, []) { Owner = this }.ShowDialog();

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

        var body = $"{location.RequestedPath}\n\n{location.Explanation}";

        // Recommended first, and here the recommendation IS to choose another folder: nothing
        // has been destroyed and the user is mid-way through opening something. That is the
        // one place in this file where the affirmative leads.
        var window = new GuardPromptWindow(
            caption,
            body,
            [new PromptOption("Choose another folder…", true), new PromptOption("Cancel", false)],
            cancels: false)
        {
            Owner = this,
        };

        window.ShowDialog();

        if (window.Result is true)
        {
            await PickRepositoryAsync();
        }
    }

    /// <summary>
    /// Give the log's row a height, and a splitter to drag it with.
    /// </summary>
    /// <remarks>
    /// <b>The half of "the log is open" that a panel cannot do for itself.</b> Showing the list
    /// and turning the chevron belong to <c>LogPanelView</c>; the row and the splitter are this
    /// window's grid. Both listen to <c>IsExpanded</c> independently rather than one calling
    /// the other, so neither has to know the other exists.
    /// <para>
    /// A GridSplitter cannot resize an Auto row, so the row carries a pixel height while the
    /// log is open and goes back to Auto - header only - when it closes. The splitter follows,
    /// because a drag handle above something that cannot grow is a control that lies about what
    /// it does.
    /// </para>
    /// </remarks>
    private void ApplyLogRowHeight()
    {
        var expanded = _shell.Log.IsExpanded;

        LogSplitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        LogRow.Height = expanded
            ? new GridLength(LogHeight(), GridUnitType.Pixel)
            : GridLength.Auto;
    }

    /// <summary>The log height to open at: what was stored, or the default.</summary>
    /// <remarks>
    /// Clamped against the window's own height rather than trusted. Somebody who dragged the
    /// log to 900 px on a 4K monitor and then opened Versio on a laptop would otherwise get a
    /// log console taller than the window, the panels squeezed to nothing, and no splitter
    /// left on screen to drag it back with.
    /// </remarks>
    private double LogHeight()
    {
        const double Default = 200;

        var available = ActualHeight > 0 ? ActualHeight * 0.6 : 0;
        var restored = LayoutSettings.Restore(_layout.LogHeight, minimum: 80, available);

        return restored > 0 ? restored : Default;
    }

    /// <summary>What the user has dragged this session, ready to be written down.</summary>
    private LayoutSettings CurrentLayout() => new()
    {
        // Only while it is open. A collapsed log's row is Auto and reads back as the header's
        // height, which would quietly shrink the log a little on every launch that ended with
        // it closed.
        LogHeight = _shell.Log.IsExpanded ? LogRow.ActualHeight : _layout.LogHeight,
        PanelWidth = PanelColumn.ActualWidth,
    };

    private LayoutSettings _layout;

    /// <summary>Put the panels back where the user left them.</summary>
    /// <remarks>
    /// The width is clamped the same way the log height is, and for the same reason: a stored
    /// size is only as trustworthy as the display it was stored on.
    /// </remarks>
    private void ApplyStoredLayout()
    {
        var width = LayoutSettings.Restore(
            _layout.PanelWidth, minimum: PanelColumn.MinWidth, available: ActualWidth * 0.6);

        if (width > 0)
        {
            PanelColumn.Width = new GridLength(width, GridUnitType.Pixel);
        }

        ApplyLogRowHeight();
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
        ExplorerPanel.Visibility = Show(panel == ShellPanel.Explorer);
        GitPanel.Visibility = Show(panel == ShellPanel.Git);
        HistoryPanel.Visibility = Show(panel == ShellPanel.History);

        // The change count is no longer this method's business. It was a pill in the panel
        // title shown here on the Project panel alone, which meant the one number saying the
        // working tree had anything in it was hidden from the two panels where you cannot
        // otherwise see it. It is on the git rail now, bound to ChangeBadge and visible from
        // every panel, so nothing about it depends on which one is showing.

        static Visibility Show(bool visible) =>
            visible ? Visibility.Visible : Visibility.Collapsed;
    }

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

    /// <summary>
    /// Close the popup before the check runs; the button's command does the checking.
    /// </summary>
    /// <remarks>
    /// The same reason as <see cref="OnOpenSystemInfo"/>: the check may end in a modal - the
    /// update offer - and one opening behind an open popup leaves the popup hanging over it.
    /// Click is raised before the command executes, so the popup is gone first.
    /// </remarks>
    private void OnCheckForUpdates(object sender, RoutedEventArgs e) =>
        SettingsPopup.IsOpen = false;

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        SyncThemeRadios();
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    /// <summary>Apply the theme, and remember it.</summary>
    /// <remarks>
    /// Remembering is the half that was missing. <c>ThemeManager.Apply</c> has always worked;
    /// nothing wrote the choice down, so picking Dark on a light-mode Windows held for the
    /// session and was gone on the next launch - with this popup showing "Follow system"
    /// checked, as though the user had chosen it.
    /// <para>
    /// The write is not awaited, because a Checked handler cannot. It costs the preference and
    /// nothing else if it fails: the store never throws, and the theme is already applied.
    /// </para>
    /// </remarks>
    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } &&
            Enum.TryParse<AppTheme>(tag, out var theme))
        {
            ThemeManager.Apply(theme);
            _ = _shell.RememberThemeAsync(tag);
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
