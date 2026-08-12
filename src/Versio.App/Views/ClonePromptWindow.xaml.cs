using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Versio.Core.Git;

namespace Versio.App.Views;

/// <summary>
/// Clone a repository: verify the remote first, then download it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clone stays disabled until Verify passes</b> (plan §9.2). Not ceremony: a clone of a
/// repository full of PLC binaries runs for minutes, and the two failures anybody actually
/// hits — a typo in the URL, and credentials that do not work here — both surface in
/// <c>ls-remote</c> in under a second. Finding out at the end of a download is the worst
/// possible moment.
/// </para>
/// <para>
/// <b>Git runs through delegates supplied by the host</b>, not from this window. The view stays
/// a view: it knows the order of the steps and what to grey out, and nothing about processes,
/// credentials or logging. It is also what lets the dialog be driven by a test.
/// </para>
/// </remarks>
public partial class ClonePromptWindow : Window
{
    private readonly Func<string, CancellationToken, Task<GitResult>> _verify;
    private readonly Func<string, string, IProgress<GitProgress>?, CancellationToken, Task<GitResult>> _clone;

    /// <summary>Where the clone landed, once it has succeeded.</summary>
    public string? ClonedPath { get; private set; }

    /// <summary>The URL that <see cref="_verify"/> last approved, so an edit can invalidate it.</summary>
    private string? _verifiedUrl;

    /// <summary>
    /// Waits for typing to stop before verifying.
    /// </summary>
    /// <remarks>
    /// Verification is automatic: pasting a URL should not then require pressing a button to
    /// find out whether it is any good. But a keystroke is not a URL, and verifying on every
    /// one would spawn a git process per character and paint the box red while somebody is
    /// still halfway through typing it. So the timer restarts on each edit and only fires once
    /// the typing has stopped - and <see cref="LooksComplete"/> keeps it quiet until there is
    /// something worth asking about.
    /// </remarks>
    private readonly DispatcherTimer _verifyAfterTyping = new()
    {
        Interval = TimeSpan.FromMilliseconds(600),
    };

    public ClonePromptWindow(
        Func<string, CancellationToken, Task<GitResult>> verify,
        Func<string, string, IProgress<GitProgress>?, CancellationToken, Task<GitResult>> clone,
        string? initialFolder = null)
    {
        _verify = verify;
        _clone = clone;

        InitializeComponent();

        FolderBox.Text = initialFolder ?? string.Empty;

        _verifyAfterTyping.Tick += async (_, _) =>
        {
            _verifyAfterTyping.Stop();
            await VerifyAsync();
        };

        // Once up front: TextChanged does not fire for the initial values, and a dialog that
        // opens with two blank explanation lines reads as broken rather than as waiting.
        UpdateDestination();

        Loaded += (_, _) => UrlBox.Focus();
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        // Editing the URL invalidates the verification it had. Without this, verifying one
        // remote and then pasting another would clone the second on the strength of the
        // first - the failure this whole dialog is shaped to prevent.
        _verifiedUrl = null;
        VerifyButton.IsEnabled = !string.IsNullOrWhiteSpace(UrlBox.Text);
        UpdateDestination();

        // Restarted on every keystroke, so it fires once when typing stops rather than once
        // per character.
        _verifyAfterTyping.Stop();

        if (LooksComplete(UrlBox.Text))
        {
            VerifyMessage.Text = string.Empty;
            _verifyAfterTyping.Start();
            return;
        }

        // Half a URL is not a failure, and saying "repository not found" at somebody who is
        // still typing trains them to ignore the line that will later carry the real answer.
        VerifyMessage.Text = UrlBox.Text.Trim().Length == 0
            ? string.Empty
            : "Waiting for the rest of the address…";
        VerifyMessage.Foreground =
            (System.Windows.Media.Brush)FindResource("Brush.Text.Disabled");
    }

    /// <summary>
    /// True when a URL is complete enough to be worth asking git about.
    /// </summary>
    /// <remarks>
    /// The two shapes that reach a remote: a scheme with a host and something after it
    /// (<c>https://host/owner/repo</c>), and the scp-like form (<c>git@host:owner/repo</c>).
    /// A local path counts too, because a folder is a perfectly good remote and is how the
    /// clone path is tested. Anything shorter is somebody mid-keystroke.
    /// </remarks>
    internal static bool LooksComplete(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length < 4)
        {
            return false;
        }

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            // Something after the host, not just "https://".
            var rest = text[(scheme + 3)..];
            var slash = rest.IndexOf('/');
            return slash > 0 && rest.Length > slash + 1;
        }

        if (text.Contains('@') && text.Contains(':'))
        {
            return text.Length > text.IndexOf(':') + 1;
        }

        // A local path, absolute or UNC.
        return Path.IsPathRooted(text) && Directory.Exists(text);
    }

    private void OnFolderChanged(object sender, TextChangedEventArgs e) => UpdateDestination();

    /// <summary>
    /// The click handlers are thin wrappers over awaitable work.
    /// </summary>
    /// <remarks>
    /// An <c>async void</c> handler is un-awaitable by anything, including a test: the click
    /// returns at the first suspension point and the rest happens whenever the dispatcher next
    /// pumps. Driving this dialog through the handlers alone left continuations running after
    /// the test that started them had finished, and took the test host down with it. The work
    /// returns a Task; the handler is the only <c>async void</c> and does nothing but start it.
    /// </remarks>
    private async void OnVerify(object sender, RoutedEventArgs e) => await VerifyAsync();

    private async void OnClone(object sender, RoutedEventArgs e) => await CloneAsync();

    internal async Task VerifyAsync()
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        Say(VerifyMessage, "Checking…", alarming: false);
        SetBusy(true);

        var result = await _verify(url, CancellationToken.None);

        SetBusy(false);

        if (result.Success)
        {
            _verifiedUrl = url;
            Say(VerifyMessage, "Reachable, and the credentials work.", alarming: false);
        }
        else
        {
            _verifiedUrl = null;

            // Git's own words. They name the actual problem — unknown host, authentication
            // failed, repository not found — far better than anything invented here.
            Say(VerifyMessage, result.FailureReason ?? "That remote could not be reached.",
                alarming: true);
        }

        UpdateDestination();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Clone into which folder?",
            Multiselect = false,
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : string.Empty,
        };

        if (picker.ShowDialog(this) == true)
        {
            FolderBox.Text = picker.FolderName;
        }
    }

    internal async Task CloneAsync()
    {
        // Re-checked rather than trusted from the button: Clone is IsDefault, so Enter
        // reaches this whatever the button's enabled state is showing.
        if (Destination() is not { } destination || _verifiedUrl is null)
        {
            return;
        }

        SetBusy(true);
        Say(DestinationMessage, $"Cloning into {destination}…", alarming: false);

        var progress = new Progress<GitProgress>(p =>
            Say(DestinationMessage,
                p.Percent is { } percent ? $"{p.Phase} {percent}%" : p.Phase,
                alarming: false));

        var result = await _clone(_verifiedUrl, destination, progress, CancellationToken.None);

        SetBusy(false);

        if (!result.Success)
        {
            Say(DestinationMessage, result.FailureReason ?? "The clone failed.", alarming: true);
            return;
        }

        ClonedPath = destination;
        Finish(accepted: true);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ClonedPath = null;
        Finish(accepted: false);
    }

    /// <summary>Close, if this window was shown as a dialog.</summary>
    /// <remarks>
    /// <c>DialogResult</c> exists only for a window shown with <c>ShowDialog</c>, and setting
    /// it on any other throws. The guard keeps the window usable from a host that shows it
    /// some other way, and it is what lets the whole verify-then-clone flow be driven by a
    /// test with nothing on screen. <see cref="ClonedPath"/> is set first either way, because
    /// that is the result — closing is how it gets delivered, not what it is.
    /// </remarks>
    private void Finish(bool accepted)
    {
        if (IsLoaded)
        {
            DialogResult = accepted;
        }
    }

    /// <summary>The full path the clone will create, or null when it cannot be worked out.</summary>
    /// <remarks>
    /// The folder name comes from the URL's last segment with any <c>.git</c> removed, which is
    /// what git itself would do. Shown rather than assumed: a parent folder plus a name derived
    /// from a URL is two guesses, and the user should see the answer before pressing a button
    /// that writes to disk.
    /// </remarks>
    internal string? Destination()
    {
        var folder = FolderBox.Text.Trim();
        var url = UrlBox.Text.Trim();

        if (folder.Length == 0 || url.Length == 0)
        {
            return null;
        }

        var name = RepositoryNameFrom(url);
        return name.Length == 0 ? null : Path.Combine(folder, name);
    }

    /// <summary>The repository name git would derive from a URL.</summary>
    internal static string RepositoryNameFrom(string url)
    {
        var trimmed = url.TrimEnd('/', '\\', ' ');

        // Both separators, and the scp-like form (git@host:owner/repo.git) whose last
        // separator is a colon rather than a slash.
        var cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var last = cut < 0 ? trimmed : trimmed[(cut + 1)..];

        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? last[..^4]
            : last;
    }

    private void UpdateDestination()
    {
        var destination = Destination();

        DestinationMessage.Text = destination is null
            ? "Choose a folder, and the repository name will be taken from the URL."
            : destination;

        DestinationMessage.Foreground =
            (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary");

        // The gate: a destination AND a verification of the URL currently in the box.
        CloneButton.IsEnabled = destination is not null && _verifiedUrl == UrlBox.Text.Trim();
    }

    /// <summary>Show a line, in the calm colour or the alarming one.</summary>
    private void Say(TextBlock target, string text, bool alarming)
    {
        target.Text = text;
        target.Foreground = (System.Windows.Media.Brush)FindResource(
            alarming ? "Brush.Status.Deleted" : "Brush.Text.Secondary");
    }

    /// <summary>
    /// Dim the whole dialog while git is running.
    /// </summary>
    /// <remarks>
    /// Both buttons, not just the one pressed: verifying while a clone is downloading, or
    /// starting a second clone, are both reachable otherwise and neither is meaningful. The
    /// same rule the git panel applies with its shared busy flag.
    /// </remarks>
    private void SetBusy(bool busy)
    {
        VerifyButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(UrlBox.Text);
        CloneButton.IsEnabled = !busy && Destination() is not null &&
                                _verifiedUrl == UrlBox.Text.Trim();
        UrlBox.IsEnabled = !busy;
        FolderBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}
