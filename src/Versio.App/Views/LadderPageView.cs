using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Versio.App.ViewModels;
using Versio.Core.Iec.Ladder;

namespace Versio.App.Views;

/// <summary>
/// A ladder block's Rendered view: its networks drawn by the browser engine, old beside new.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a browser.</b> The diagram is SVG written by <see cref="LadderHtml"/> in Core, the same
/// markup the Outpost React page will show, so both places draw the same rung. A browser also
/// gives zoom (Ctrl+wheel, Ctrl+0), selection and copy of operand text, and container queries
/// that stack old above new on a narrow pane.
/// </para>
/// <para>
/// <b>What the page may do</b>, the walls <see cref="MarkdownPageView"/> keeps. Every character
/// from the file is escaped; the content-security policy allows script only from the app's own
/// origin; every request is answered from memory and anything else refused, so nothing reaches a
/// network; and the page never navigates. Its one script is <see cref="LadderPageAssets.Script"/>:
/// navigation between changes, "changes only", and the two sides scrolling together.
/// </para>
/// <para>
/// <b>No WebView2 runtime</b> leaves a sentence in the pane, not an exception: the Raw tab still
/// has the file.
/// </para>
/// </remarks>
public sealed class LadderPageView : Border
{
    public static readonly DependencyProperty LadderProperty =
        DependencyProperty.Register(nameof(Ladder), typeof(LadderRender), typeof(LadderPageView),
            new PropertyMetadata(null, (d, _) => ((LadderPageView)d).Render()));

    public static readonly DependencyProperty ReportCommandProperty =
        DependencyProperty.Register(nameof(ReportCommand), typeof(ICommand), typeof(LadderPageView));

    private static readonly DependencyProperty ThemeBrushProperty =
        DependencyProperty.Register("ThemeBrush", typeof(Brush), typeof(LadderPageView),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.OldValue is not null)
                {
                    ((LadderPageView)d).Render();
                }
            }));

    private static readonly byte[] PageScript = Encoding.UTF8.GetBytes(LadderPageAssets.Script);

    private WebView2CompositionControl? _browser;
    private Task? _starting;
    private byte[] _page = [];
    private string? _shownHtml;
    private bool _unavailable;

    public LadderPageView()
    {
        SetResourceReference(ThemeBrushProperty, "Brush.Surface.Base");
        SetResourceReference(BackgroundProperty, "Brush.Surface.Base");
        IsVisibleChanged += (_, _) => Render();
    }

    public LadderRender? Ladder
    {
        get => (LadderRender?)GetValue(LadderProperty);
        set => SetValue(LadderProperty, value);
    }

    /// <summary>Where the view reports what it did, for the log.</summary>
    public ICommand? ReportCommand
    {
        get => (ICommand?)GetValue(ReportCommandProperty);
        set => SetValue(ReportCommandProperty, value);
    }

    /// <summary>The last page built, for a test to read without a browser.</summary>
    internal string PageHtml => Encoding.UTF8.GetString(_page);

    private void Report(string message)
    {
        if (ReportCommand is { } command && command.CanExecute(message))
        {
            command.Execute(message);
        }
    }

    private async void Render()
    {
        if (Ladder is not { } ladder || _unavailable)
        {
            return;
        }

        // Pending: the committed copy is still being read. An empty page until it lands, never
        // a page that says "no changes" about a block that has some. And empty, not skipped:
        // WPF reuses this view when the next selection is also a ladder, so skipping would
        // leave the previous block's diagram on screen under the new block's name.
        var fragment = ladder.Pending ? string.Empty : LadderHtml.Fragment(ladder.Comparison, ladder.Style);
        var html = LadderPage.Build(fragment, this);

        // Raw then back to Rendered changes visibility and nothing else; reloading the same page
        // would lose where the reader had scrolled to.
        if (_browser is not null && IsVisible && string.Equals(html, _shownHtml, StringComparison.Ordinal))
        {
            return;
        }

        _page = Encoding.UTF8.GetBytes(html);
        if (!IsVisible)
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
            // Every failure, from an async void handler: an uncaught one would take the window down.
            _unavailable = true;
            Report($"ladder page view could not start: {ex.GetType().Name}: {ex.Message}");
            Child = new TextBlock
            {
                Text = "The diagram needs the WebView2 runtime, which did not start. The Raw tab has the file.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12),
            };
            return;
        }

        if (_browser?.CoreWebView2 is { } core)
        {
            _shownHtml = html;
            core.Navigate(LadderPage.Origin + "/page.html");
        }
    }

    private async Task Start()
    {
        var environment = await BrowserEnvironment.GetAsync().ConfigureAwait(true);

        // Composition, not HWND: the page lives inside the pane's layout, under the menus and toasts.
        var browser = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
        };
        Child = browser;
        await browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;   // copy an operand
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsZoomControlEnabled = true;            // Ctrl+wheel, Ctrl+0
        core.Settings.IsWebMessageEnabled = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var (body, type) = e.Request.Uri switch
            {
                LadderPage.Origin + "/page.html" => (_page, "text/html; charset=utf-8"),
                LadderPage.Origin + "/ladder.js" => (PageScript, "text/javascript; charset=utf-8"),
                _ => ((byte[]?)null, string.Empty),
            };

            e.Response = body is null
                ? core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty)
                : core.Environment.CreateWebResourceResponse(
                    new MemoryStream(body, writable: false), 200, "OK", $"Content-Type: {type}");
        };

        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(LadderPage.Origin + "/page.html", StringComparison.Ordinal))
            {
                e.Cancel = true;
                Report($"ladder page refused a navigation: {e.Uri}");
            }
        };

        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
            {
                Report($"ladder page failed to load: {e.WebErrorStatus}");
            }
        };

        _browser = browser;
    }
}
