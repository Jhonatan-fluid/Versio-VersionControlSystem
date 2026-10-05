using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Versio.Core.Text;

namespace Versio.App.Views;

/// <summary>
/// The Rendered view of a Markdown file, drawn by the browser engine: tables laid out by
/// content and Mermaid diagrams drawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a browser, after a FlowDocument shipped first.</b> Seen on 2026-10-04 on
/// <c>docs/outpost/PLAN.md</c>: the FlowDocument gave a four-column table a last column of
/// almost no width, so each row stood hundreds of pixels tall, and the Mermaid flowchart above
/// it was its source text. FlowDocument tables have no automatic column layout, and mermaid is
/// JavaScript - both are things only a browser does properly. <see cref="MarkdownView"/> stays
/// as the fallback when the WebView2 runtime cannot start, so a document still opens.
/// </para>
/// <para>
/// <b>What the page may do.</b> It runs script, because mermaid is script, so the walls are:
/// every character of the document escaped (<see cref="MarkdownHtml"/>); a content-security
/// policy allowing script only from the app's own origin (<see cref="MarkdownPage"/>); every
/// request answered from memory and anything else refused, so nothing reaches a network; no
/// navigation at all - a link posts its raw target to <see cref="LinkCommand"/>, the same logged
/// door as before, and the app decides.
/// </para>
/// <para>
/// <b>The theme is watched through a resource reference.</b> <c>ThemeBrush</c> is bound by
/// reference to the window background; a theme switch changes it, and the change rebuilds the
/// page with the new colours. Nothing else here knows a theme exists.
/// </para>
/// </remarks>
public sealed class MarkdownPageView : Border
{
    public static readonly DependencyProperty BlocksProperty =
        DependencyProperty.Register(nameof(Blocks), typeof(IReadOnlyList<MdBlock>), typeof(MarkdownPageView),
            new PropertyMetadata(null, (d, _) => ((MarkdownPageView)d).Render()));

    public static readonly DependencyProperty LinkCommandProperty =
        DependencyProperty.Register(nameof(LinkCommand), typeof(ICommand), typeof(MarkdownPageView));

    public static readonly DependencyProperty ReportCommandProperty =
        DependencyProperty.Register(nameof(ReportCommand), typeof(ICommand), typeof(MarkdownPageView));

    private static readonly DependencyProperty ThemeBrushProperty =
        DependencyProperty.Register("ThemeBrush", typeof(Brush), typeof(MarkdownPageView),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.OldValue is not null)
                {
                    ((MarkdownPageView)d).Render();
                }
            }));

    private static readonly Lazy<byte[]> Mermaid = new(() =>
    {
        using var stream = typeof(MarkdownPageView).Assembly
            .GetManifestResourceStream("Versio.App.Assets.mermaid.mermaid.min.js")
            ?? throw new InvalidOperationException("mermaid.min.js is not embedded in the assembly");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    private static readonly byte[] PageScript = Encoding.UTF8.GetBytes(MarkdownPage.Script);

    private WebView2CompositionControl? _browser;
    private Task? _starting;
    private byte[] _page = [];
    private string? _shownHtml;
    private bool _fallback;

    public MarkdownPageView()
    {
        SetResourceReference(ThemeBrushProperty, "Brush.Surface.Base");
        SetResourceReference(BackgroundProperty, "Brush.Surface.Base");
        IsVisibleChanged += (_, _) => Render();
    }

    public IReadOnlyList<MdBlock>? Blocks
    {
        get => (IReadOnlyList<MdBlock>?)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>Where a pressed link goes; the parameter is its raw target.</summary>
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
        if (Blocks is not { } blocks)
        {
            return;
        }

        var html = MarkdownPage.Build(MarkdownHtml.Fragment(blocks), PagePalette.FromResources(this));

        // Raw then back to Rendered changes visibility and nothing else. Reloading the same
        // page would throw away where the reader had scrolled to.
        if (_browser is not null && IsVisible && string.Equals(html, _shownHtml, StringComparison.Ordinal))
        {
            return;
        }

        _page = Encoding.UTF8.GetBytes(html);

        if (_fallback)
        {
            ShowFallback(blocks);
            return;
        }

        // Nobody looking: build the page (cheap) but do not start a browser for it.
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
            // Every failure, from an async void handler: an uncaught one would take the window
            // down. The document still opens, drawn the older way.
            _fallback = true;
            Report($"markdown page view could not start, drawing without a browser: {ex.GetType().Name}: {ex.Message}");
            ShowFallback(blocks);
            return;
        }

        if (_browser?.CoreWebView2 is { } core)
        {
            _shownHtml = html;
            core.Navigate(MarkdownPage.Origin + "/page.html");
        }
    }

    private void ShowFallback(IReadOnlyList<MdBlock> blocks)
    {
        if (Child is MarkdownView existing)
        {
            existing.LinkCommand = LinkCommand;
            existing.Blocks = blocks;
            return;
        }

        Child = new MarkdownView { Blocks = blocks, LinkCommand = LinkCommand };
    }

    private async Task Start()
    {
        var environment = await BrowserEnvironment.GetAsync().ConfigureAwait(true);

        // Composition, not HWND: the page lives inside the pane's layout, under the branch menu
        // and the toasts, and an HWND would sit on top of them.
        var browser = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
        };
        Child = browser;
        await browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;   // copy, select all
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsZoomControlEnabled = true;

        // Served from memory, all of it. Anything not ours is refused with a 403, so a
        // diagram or a stray reference cannot make the page reach out.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            var (body, type) = uri switch
            {
                MarkdownPage.Origin + "/page.html" => (_page, "text/html; charset=utf-8"),
                MarkdownPage.Origin + "/mermaid.js" => (Mermaid.Value, "text/javascript; charset=utf-8"),
                MarkdownPage.Origin + "/page.js" => (PageScript, "text/javascript; charset=utf-8"),
                _ => ((byte[]?)null, string.Empty),
            };

            e.Response = body is null
                ? core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty)
                : core.Environment.CreateWebResourceResponse(
                    new MemoryStream(body, writable: false), 200, "OK", $"Content-Type: {type}");
        };

        // The page never navigates. Its links post to the app instead (see MarkdownPage.Script).
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(MarkdownPage.Origin + "/page.html", StringComparison.Ordinal))
            {
                e.Cancel = true;
                Report($"markdown page refused a navigation: {e.Uri}");
            }
        };

        core.NewWindowRequested += (_, e) => e.Handled = true;

        core.WebMessageReceived += (_, e) =>
        {
            string target;
            try
            {
                target = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }

            if (LinkCommand is { } command && command.CanExecute(target))
            {
                command.Execute(target);
            }
        };

        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
            {
                Report($"markdown page failed to load: {e.WebErrorStatus}");
            }
        };

        _browser = browser;
    }
}
