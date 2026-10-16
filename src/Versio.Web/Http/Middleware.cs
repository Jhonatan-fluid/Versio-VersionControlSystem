using System.Diagnostics;
using System.Globalization;
using Versio.Core.Logging;

namespace Versio.Web.Http;

/// <summary>
/// The headers every response carries (docs/web/PLAN.md, security rules 6 and 13).
/// </summary>
/// <remarks>
/// <para>
/// <b>The content-security policy is the second wall behind the Core renderers' escaping</b>, the
/// same role it plays in the desktop's WebView2 pages: script only from this origin, no inline
/// script, no inline stylesheet, nothing framed, no plugin. Style <i>attributes</i> are allowed
/// (<c>style-src-attr</c>) because two Core renderers emit them on purpose, the ladder's width
/// variable and Markdown's table alignment; an attribute cannot run script, and its value is
/// escaped. Images come from here and from GitHub's avatar host, for the signed-in face.
/// </para>
/// <para>
/// <b><c>Referrer-Policy: no-referrer</c></b>, because this site's URLs name private repositories
/// and file paths, and a link out would otherwise hand them to whoever it points at.
/// <b><c>X-Robots-Tag: noindex</c></b>, so no search engine keeps a page even if a link to a
/// private view leaks. <b><c>no-store</c></b> on the API and sign-in, so no proxy or CDN keeps a
/// response meant for one person (security rule 2).
/// </para>
/// </remarks>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; "
        + "img-src 'self' data: https://avatars.githubusercontent.com; font-src 'self'; connect-src 'self'; "
        + "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    /// <summary>The diagram frame's path, the one page with a policy of its own.</summary>
    public const string MermaidFrame = "/mermaid-frame.html";

    /// <summary>
    /// The diagram frame's policy. Mermaid writes an inline &lt;style&gt; into every diagram, which
    /// <see cref="Csp"/> rightly refuses; loosening the page's policy would let any injected CSS
    /// run there. So diagrams render in a frame the page loads with <c>sandbox="allow-scripts"</c>
    /// (no same-origin: no cookies, no session, no reach into the page), and only this response
    /// allows inline styles. It may be framed by this site and no other.
    /// </summary>
    /// <remarks>
    /// <b>The site's origin is named, not <c>'self'</c>.</b> A frame sandboxed without
    /// allow-same-origin has an opaque origin, and <c>'self'</c> then matches nothing, so the first
    /// version's frame could not load its own scripts and no diagram ever drew (seen in the
    /// browser). <c>{0}</c> is the site's origin: the configured public URL, or the request's.
    /// </remarks>
    public const string FrameCsp =
        "default-src 'none'; script-src {0}; style-src {0} 'unsafe-inline'; img-src data:; font-src data:; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'self'";

    /// <summary>
    /// The raw document route's policy: inert bytes that the Docs page frames (the embedded
    /// PDF), so they may be framed by this site — and only this site. Everything else about the
    /// response stays shut; the content rules themselves are on <c>Documents</c>.
    /// </summary>
    /// <remarks>
    /// <c>'self'</c> works here where the diagram frame needed the origin spelled out: the Docs
    /// page embeds this route from the site's real origin, not from a sandboxed frame's opaque
    /// one. Found by the owner in a real browser on 2026-10-16 — the blanket DENY below blocked
    /// the viewer's own PDF frame, which Chrome reports as "refused to connect" while the
    /// built-in browser showed a silent white box (FINDINGS).
    /// </remarks>
    public const string RawCsp = "default-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// Whether this is the raw document route: /api/repos/{owner}/{repo}/raw, five segments. A
    /// repository itself named "raw" has four and keeps the ordinary headers.
    /// </summary>
    private static bool IsRawDocument(PathString path) =>
        path.StartsWithSegments("/api/repos")
        && path.Value is { } p
        && p.EndsWith("/raw", StringComparison.OrdinalIgnoreCase)
        && p.Count(c => c == '/') == 5;

    /// <summary>The frame's policy for this request's site.</summary>
    public static string FramePolicy(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configured = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<WebOptions>>()?.Value.GitHub.PublicBaseUrl;
        var origin = Uri.TryCreate(configured, UriKind.Absolute, out var u)
            ? u.GetLeftPart(UriPartial.Authority)
            : $"{context.Request.Scheme}://{context.Request.Host}";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, FrameCsp, origin);
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            var path = context.Request.Path;
            var frame = path.Equals(MermaidFrame, StringComparison.OrdinalIgnoreCase);
            var raw = IsRawDocument(path);
            h.ContentSecurityPolicy = frame ? FramePolicy(context) : raw ? RawCsp : Csp;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = frame || raw ? "SAMEORIGIN" : "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Robots-Tag"] = "noindex, nofollow";
            h["Cross-Origin-Opener-Policy"] = "same-origin";

            // The sandboxed frame has an opaque origin, so its script requests look cross-origin;
            // its two scripts (a public library and the frame's glue) carry nothing private.
            h["Cross-Origin-Resource-Policy"] =
                path.Equals("/assets/mermaid.js", StringComparison.OrdinalIgnoreCase) || path.Equals("/mermaid-frame.js", StringComparison.OrdinalIgnoreCase)
                    ? "cross-origin"
                    : "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/auth"))
            {
                h.CacheControl = "private, no-store";
            }

            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>
/// One log line per request, under its own operation id: what was asked, how it ended, how long
/// it took (AGENTS.md, "anything that runs must say so in the log").
/// </summary>
/// <remarks>
/// <b>The path only, never the query.</b> The OAuth callback carries a one-time code and a state
/// in its query, and nothing that could become a credential reaches the log. Static files are
/// not logged: quiet when idle, loud when working.
/// </remarks>
public sealed class RequestLog(RequestDelegate next, IVersioLogger log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/auth"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        using var operation = log.BeginOperation(out _);
        var clock = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error(LogCategory.Web, $"{context.Request.Method} {path} failed", e);
            throw;
        }
        finally
        {
            var status = context.Response.StatusCode;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{context.Request.Method} {path} -> {status} in {clock.ElapsedMilliseconds} ms");
            if (status >= 500)
            {
                log.Warn(LogCategory.Web, line);
            }
            else
            {
                log.Info(LogCategory.Web, line);
            }
        }
    }
}

/// <summary>Log lines on standard output, where a container's logs are read.</summary>
public sealed class ConsoleLogSink : ILogSink
{
    private readonly object _gate = new();

    public void Emit(LogEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            Console.Out.WriteLine(entry.Format());
        }
    }
}
