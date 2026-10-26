namespace Versio.Web.Api;

/// <summary>
/// What the Documentation tab (docs/web/PLAN.md, "The product shape" item 4) treats as a
/// document, and how its bytes may be served.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serving repository bytes from this origin is the classic self-XSS</b>, which is why GitHub
/// serves raw content from another domain and why this list is a narrow allowlist rather than
/// "any file". Three rules keep it safe here: the content type is OURS, chosen by extension from
/// this table, never sniffed or taken from the repository (with <c>X-Content-Type-Options:
/// nosniff</c> site-wide a browser will not second-guess it); nothing is served as SVG or any
/// other type a browser would execute in THIS origin; and everything except PDF and the web
/// page is an attachment, so it downloads instead of rendering. PDF is inline because the
/// browser's PDF viewer renders it out of process and a file lying about being a PDF just fails
/// to draw. <c>.html</c> is inline since Step 9 (the Web page tab), and its one wall is the
/// <c>Content-Security-Policy: sandbox</c> the middleware puts on exactly those answers: an
/// opaque origin, no scripts, no forms — pinned by <c>RenderedKindsTests</c>. The middleware
/// chooses by the request's path and this table by the file's, and the two must agree, which
/// <c>RawDocumentTests</c> asserts (review, 2026-10-24: this remark said "never HTML" for a
/// week after it was).
/// </para>
/// <para>
/// Office formats are downloads, deliberately: a browser cannot draw them, and converting them
/// server-side would drag an office suite's worth of parsing into a server kept lean on purpose
/// (the product shape, item 4 — rejected there).
/// </para>
/// </remarks>
public static class Documents
{
    /// <summary>How a document is served: its content type, and whether the browser may show it inline.</summary>
    public readonly record struct Disposition(string ContentType, bool Inline);

    /// <summary>
    /// The disposition for a path, or null when it is not a document the raw route serves.
    /// Text documents are here too — the page renders those through <c>/view</c>, but their
    /// download is served as plain text, which nothing executes.
    /// </summary>
    public static Disposition? DispositionFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var dot = path.LastIndexOf('.');
        if (dot < 0 || path.LastIndexOf('/') > dot)
        {
            return null;
        }

        return path[dot..].ToLowerInvariant() switch
        {
            ".pdf" => new Disposition("application/pdf", Inline: true),
            // A web page, served inline for the page to frame sandboxed; the security headers
            // middleware adds the sandbox policy to every HTML answer of this route.
            ".html" or ".htm" => new Disposition("text/html", Inline: true),
            ".md" or ".markdown" or ".txt" or ".csv" => new Disposition("text/plain", Inline: false),
            ".docx" or ".doc" or ".xlsx" or ".xls" or ".pptx" or ".ppt"
                or ".odt" or ".ods" or ".odp" or ".rtf" => new Disposition("application/octet-stream", Inline: false),
            _ => null,
        };
    }
}
