using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Versio.Core.Text;

namespace Versio.App.Views;

/// <summary>The theme colours and fonts a rendered Markdown page is drawn with.</summary>
internal sealed record PagePalette(
    string Background,
    string Text,
    string Secondary,
    string Border,
    string Raised,
    string Sunken,
    string Accent,
    string UiFont,
    string MonoFont,
    bool Dark)
{
    /// <summary>Read from the app's live theme resources, so the page matches the window.</summary>
    public static PagePalette FromResources(FrameworkElement source)
    {
        string Colour(string key, string fallback) =>
            source.TryFindResource(key) is SolidColorBrush { Color: var c }
                ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                : fallback;

        string Font(string key, string fallback) =>
            source.TryFindResource(key) is FontFamily f ? f.Source : fallback;

        var background = Colour("Brush.Surface.Base", "#1E1F22");

        return new PagePalette(
            background,
            Colour("Brush.Text.Primary", "#DFE1E5"),
            Colour("Brush.Text.Secondary", "#9DA0A8"),
            Colour("Brush.Border.Subtle", "#393B40"),
            Colour("Brush.Surface.Raised", "#2B2D30"),
            Colour("Brush.Surface.Sunken", "#191A1C"),
            Colour("Brush.Accent.Default", "#3574F0"),
            Font("Font.UI", "Segoe UI"),
            Font("Font.Mono", "Cascadia Mono, Consolas"),
            Luminance(background) < 0.5);
    }

    private static double Luminance(string hex)
    {
        var v = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((((v >> 16) & 0xFF) * 0.299) + (((v >> 8) & 0xFF) * 0.587) + ((v & 0xFF) * 0.114)) / 255;
    }
}

/// <summary>
/// A whole HTML document around a <see cref="MarkdownHtml"/> fragment: the theme as CSS, a
/// content-security policy, and the two scripts the page is allowed - mermaid and our own.
/// </summary>
/// <remarks>
/// <para>
/// The CSP is the second wall behind <see cref="MarkdownHtml"/>'s escaping: scripts only from
/// the app's own origin, no inline script, nothing from the network. If a document ever got
/// markup past the escaping, an injected script would still not run.
/// </para>
/// <para>
/// Fonts and colours are the app's own tokens, read when the page is built, so a document
/// reads as part of the window rather than as a web page pasted into it. Mermaid's theme
/// follows the background: its dark theme on a dark window.
/// </para>
/// </remarks>
internal static class MarkdownPage
{
    /// <summary>The origin the page and its scripts are served from. Never reaches a network.</summary>
    public const string Origin = "https://rendered.versio";

    public static string Build(string fragment, PagePalette p)
    {
        static string Css(string value) => value.Replace("\"", "'", StringComparison.Ordinal);

        var mermaidTheme = p.Dark ? "dark" : "default";

        return $$"""
            <!doctype html>
            <html data-mermaid-theme="{{mermaidTheme}}">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none'; script-src {{Origin}}; style-src 'unsafe-inline'; img-src data:; font-src data:">
            <style>
              html { background: {{p.Background}}; }
              body {
                margin: 0; padding: 14px 22px 40px; max-width: 1100px;
                font-family: "{{Css(p.UiFont)}}", "Segoe UI", sans-serif; font-size: 13.5px; line-height: 1.55;
                color: {{p.Text}}; background: {{p.Background}};
                -webkit-font-smoothing: antialiased;
              }
              h1, h2, h3, h4, h5, h6 { font-weight: 600; line-height: 1.25; margin: 22px 0 10px; }
              h1 { font-size: 1.75em; padding-bottom: .3em; border-bottom: 1px solid {{p.Border}}; }
              h2 { font-size: 1.35em; padding-bottom: .3em; border-bottom: 1px solid {{p.Border}}; }
              h3 { font-size: 1.12em; }
              h4, h5, h6 { font-size: 1em; color: {{p.Secondary}}; }
              body > :first-child { margin-top: 4px; }
              p, ul, ol, blockquote, pre, table { margin: 0 0 12px; }
              ul, ol { padding-left: 1.8em; }
              li + li { margin-top: 3px; }
              li > ul, li > ol { margin: 3px 0 0; }
              a { color: {{p.Accent}}; text-decoration: none; cursor: pointer; }
              a:hover { text-decoration: underline; }
              del { color: {{p.Secondary}}; }
              code {
                font-family: "{{Css(p.MonoFont)}}", Consolas, monospace; font-size: .9em;
                background: {{p.Raised}}; padding: .12em .35em; border-radius: 4px;
              }
              pre {
                background: {{p.Sunken}}; border: 1px solid {{p.Border}}; border-radius: 6px;
                padding: 10px 14px; overflow-x: auto; line-height: 1.45;
              }
              pre code { background: none; padding: 0; font-size: .88em; white-space: pre; }
              pre.mermaid {
                background: none; border: 1px solid {{p.Border}}; text-align: center;
                padding: 14px; color: transparent;
              }
              pre.mermaid[data-processed] { color: inherit; }
              /* Mermaid's own edge-label box is a flat grey in its dark theme; the window's
                 raised surface reads as part of the diagram instead of a sticker on it. */
              pre.mermaid .edgeLabel, pre.mermaid .edgeLabel p { background: {{p.Raised}} !important; }
              pre.mermaid .edgeLabel rect { fill: {{p.Raised}} !important; }
              blockquote {
                margin-left: 0; padding: 2px 0 2px 14px; color: {{p.Secondary}};
                border-left: 3px solid {{p.Border}};
              }
              hr { border: 0; border-top: 1px solid {{p.Border}}; margin: 18px 0; }
              table { border-collapse: collapse; display: block; overflow-x: auto; max-width: 100%; }
              th, td { border: 1px solid {{p.Border}}; padding: 5px 10px; vertical-align: top; }
              th { background: {{p.Raised}}; font-weight: 600; text-align: left; }
              tbody tr:nth-child(2n) td { background: {{p.Raised}}55; }
              ::selection { background: {{p.Accent}}55; }
            </style>
            <script src="{{Origin}}/mermaid.js"></script>
            <script src="{{Origin}}/page.js" defer></script>
            </head>
            <body>
            {{fragment}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// The page's own script: links go to the app, diagrams are drawn.
    /// </summary>
    /// <remarks>
    /// Links carry their target in <c>data-href</c> (see <see cref="MarkdownHtml"/>), so the
    /// browser has nothing to follow; a click posts the raw target to the app, which decides.
    /// Mermaid in <c>strict</c> security: labels are text, click handlers in a diagram are off.
    /// A diagram that does not parse draws mermaid's own error box rather than failing the page.
    /// </remarks>
    public const string Script = """
        document.addEventListener('click', function (e) {
          var a = e.target.closest ? e.target.closest('a[data-href]') : null;
          if (!a) return;
          e.preventDefault();
          window.chrome.webview.postMessage(a.getAttribute('data-href'));
        });
        if (window.mermaid && document.querySelector('pre.mermaid')) {
          mermaid.initialize({
            startOnLoad: false,
            securityLevel: 'strict',
            theme: document.documentElement.getAttribute('data-mermaid-theme') || 'default'
          });
          mermaid.run({ querySelector: 'pre.mermaid', suppressErrors: true });
        }
        """;
}
