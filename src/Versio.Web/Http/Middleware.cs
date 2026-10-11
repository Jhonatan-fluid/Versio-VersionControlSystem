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

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h.ContentSecurityPolicy = Csp;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Robots-Tag"] = "noindex, nofollow";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            var path = context.Request.Path;
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
