using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Versio.App.Views;

/// <summary>
/// An HTML file from the repository, drawn as a browser would draw it: the third view
/// beside Raw and Rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>The browser is created on first use, never at startup.</b> A WebView2 is a separate
/// browser process tree; paying for one in every window, for a view most sessions never open,
/// would be startup cost for nothing. It is created the first time this control is visible
/// with a file to show, and reused after that.
/// </para>
/// <para>
/// <b>Locked down, because the file is repository content, not something the user chose to
/// run.</b> Opening a branch somebody pushed must not execute their JavaScript or make network
/// requests on this machine just because a row was clicked. So: scripts off, every request
/// that is not a local file refused (no tracking pixel, no CDN, no form post), and a link
/// that would navigate away goes through <see cref="LinkCommand"/> - the same logged door as
/// a Markdown link - to the real browser, instead of turning this pane into one. A page that
/// needs script to draw anything will draw that much less; that is the price, and it is the
/// right one for a review tool. Relative CSS and images beside the file still load.
/// </para>
/// <para>
/// The browser process and its profile folder are shared with the Markdown page view; see
/// <see cref="BrowserEnvironment"/>.
/// </para>
/// </remarks>
public sealed class WebPageView : Border
{
    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(WebPageView),
            new PropertyMetadata(null, (d, _) => ((WebPageView)d).Refresh()));

    public static readonly DependencyProperty LinkCommandProperty =
        DependencyProperty.Register(nameof(LinkCommand), typeof(ICommand), typeof(WebPageView));

    public static readonly DependencyProperty ReportCommandProperty =
        DependencyProperty.Register(nameof(ReportCommand), typeof(ICommand), typeof(WebPageView));

    private WebView2CompositionControl? _browser;
    private Task? _starting;
    private string? _shown;

    public WebPageView()
    {
        IsVisibleChanged += (_, _) => Refresh();
    }

    /// <summary>The absolute path of the HTML file, or null.</summary>
    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    /// <summary>Where a link that leaves the page is sent; the parameter is its URL.</summary>
    public ICommand? LinkCommand
    {
        get => (ICommand?)GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    /// <summary>Where the view reports what it did, for the log.</summary>
    public ICommand? ReportCommand
    {
        get => (ICommand?)GetValue(ReportCommandProperty);
        set => SetValue(ReportCommandProperty, value);
    }

    /// <summary>The URI the page was navigated to, for a test to read.</summary>
    internal string? ShownPath => _shown;

    private void Report(string message)
    {
        if (ReportCommand is { } command && command.CanExecute(message))
        {
            command.Execute(message);
        }
    }

    private async void Refresh()
    {
        // Selection moved off the page: forget it, so coming back to the same file - edited
        // in the meantime - loads it again instead of showing the copy from before.
        if (FilePath is null)
        {
            _shown = null;
        }

        // Nothing to draw, or nobody looking: do not start a browser for it.
        if (!IsVisible || FilePath is not { } path || string.Equals(path, _shown, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _starting ??= Start();
            await _starting.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Every failure, not a list of expected ones: this runs from an async void
            // handler, where an exception nobody catches takes the whole window down - for a
            // view that is a convenience beside two that still work.
            // Said in the pane as well as the log: an empty rectangle would read as a page
            // that rendered blank, which is a claim about the file rather than the machine.
            _starting = null;
            Child = new TextBlock
            {
                Text = ex is WebView2RuntimeNotFoundException
                    ? "This view needs the Microsoft Edge WebView2 runtime, which is not installed on this machine. Rendered and Raw still work."
                    : "The web page view could not start. Rendered and Raw still work. The log has the reason.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
            ((TextBlock)Child).SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            Report($"web page view could not start: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // The path may have moved on while the browser was starting.
        if (_browser?.CoreWebView2 is not { } core || FilePath is not { } current)
        {
            return;
        }

        _shown = current;
        Report($"web page view loading {current}");
        core.Navigate(new Uri(current).AbsoluteUri);
    }

    private async Task Start()
    {
        var environment = await BrowserEnvironment.GetAsync().ConfigureAwait(true);

        // The composition control, not the HWND one: an HWND sits above every WPF element
        // regardless of z-order (the airspace problem), so the branch menu and the toasts
        // would open underneath the page.
        var browser = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.White };
        Child = browser;
        await browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = browser.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;

        // Every request is screened; only local files pass. A refused one gets a 403 rather
        // than a dropped connection, so the page finishes loading instead of hanging on it.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            }
        };

        // Only the file this view was asked for may be navigated to. A link the USER pressed
        // inside the page is handed to the real browser through the logged command; anything
        // the page starts by itself is refused. This forwarded every navigation, and a
        // <meta http-equiv="refresh"> needs no script to start one — so a pushed page could
        // launch the user's browser at any address on a row click (the 2026-10-24 review's
        // item 40). The decision lives in WebPageNavigation, where the suite can pin it.
        core.NavigationStarting += (_, e) =>
        {
            switch (WebPageNavigation.Decide(e.Uri, _shown, e.IsUserInitiated, e.IsRedirected))
            {
                case NavigationDecision.Allow:
                    return;

                case NavigationDecision.Forward:
                    e.Cancel = true;
                    if (LinkCommand is { } command && command.CanExecute(e.Uri))
                    {
                        command.Execute(e.Uri);
                    }

                    return;

                default:
                    e.Cancel = true;
                    Report($"web page view refused a navigation the page started itself: {e.Uri}");
                    return;
            }
        };

        // A page that opens a window gets nothing: no popup Versio does not own.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Report($"web page view refused a new window: {e.Uri}");
        };

        core.NavigationCompleted += (_, e) =>
            Report(e.IsSuccess
                ? $"web page view loaded {_shown}"
                : $"web page view failed to load {_shown}: {e.WebErrorStatus}");

        _browser = browser;
    }
}
