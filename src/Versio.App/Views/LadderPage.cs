using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Versio.Core.Iec.Ladder;

namespace Versio.App.Views;

/// <summary>
/// A whole HTML document around a <see cref="LadderHtml"/> fragment: the shared ladder
/// stylesheet, the app's theme mapped onto its variables, and a content-security policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The theme is variables, laid over the shared defaults.</b> The stylesheet is
/// <see cref="LadderPageAssets.Css"/>, the one the Outpost page will use. This only declares
/// its colour variables again from the window's own brushes, after it, so the diagram's added,
/// removed and changed marks are the Raw diff's colours and the page reads as part of the window.
/// </para>
/// <para>
/// <b>The diagram font is not the theme's.</b> <c>--ld-mono</c> keeps the shared stack: the
/// layout measured every label with that font's advance, and a different monospace would let
/// a cut label run over its neighbour.
/// </para>
/// <para>
/// <b>The CSP is the second wall behind the escaping.</b> Script only from the app's own origin,
/// no inline script, nothing from the network, the same policy as <see cref="MarkdownPage"/>.
/// </para>
/// </remarks>
internal static class LadderPage
{
    public const string Origin = "https://ladder.versio";

    public static string Build(string fragment, FrameworkElement source)
    {
        var p = PagePalette.FromResources(source);

        string Colour(string key, string fallback) =>
            source.TryFindResource(key) is SolidColorBrush { Color: var c }
                ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                : fallback;

        static string Tint(string hex, double alpha)
        {
            var v = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return string.Create(CultureInfo.InvariantCulture,
                $"rgba({(v >> 16) & 0xFF}, {(v >> 8) & 0xFF}, {v & 0xFF}, {alpha})");
        }

        static string Css(string value) => value.Replace("\"", "'", StringComparison.Ordinal);

        var added = Colour("Brush.Status.Added", p.Dark ? "#3FB950" : "#1A7F37");
        var removed = Colour("Brush.Status.Deleted", p.Dark ? "#F85149" : "#CF222E");
        var changed = Colour("Brush.Status.Modified", p.Dark ? "#D29922" : "#9A6700");
        var moved = p.Accent;

        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none'; script-src {{Origin}}; style-src 'unsafe-inline'">
            <style>
            {{LadderPageAssets.Css}}
            :root {
              --ld-fg: {{p.Text}}; --ld-bg: {{p.Background}}; --ld-muted: {{p.Secondary}};
              --ld-wire: {{p.Text}}; --ld-box: {{p.Raised}}; --ld-border: {{p.Border}};
              --ld-added: {{added}}; --ld-added-bg: {{Tint(added, 0.18)}};
              --ld-removed: {{removed}}; --ld-removed-bg: {{Tint(removed, 0.18)}};
              --ld-changed: {{changed}}; --ld-changed-bg: {{Tint(changed, 0.22)}};
              --ld-moved: {{moved}}; --ld-moved-bg: {{Tint(moved, 0.18)}};
              --ld-ui: "{{Css(p.UiFont)}}", "Segoe UI", sans-serif;
            }
            html, body { background: var(--ld-bg); }
            body { margin: 0; padding: 4px 16px 40px; }
            </style>
            <script src="{{Origin}}/ladder.js" defer></script>
            </head>
            <body>
            {{fragment}}
            </body>
            </html>
            """;
    }
}
