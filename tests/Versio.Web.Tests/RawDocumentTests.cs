using System.Net;
using Versio.Web.Api;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The raw route behind the Documentation tab (docs/web/PLAN.md, "The product shape" item 4).
/// Serving repository bytes from the page's own origin is the classic self-XSS, so these pin the
/// rules that keep it narrow: our content type by extension, nothing inline but PDF, nothing at
/// all for files off the document allowlist.
/// </summary>
public sealed class RawDocumentTests
{
    private static FakeGitHub GitHub()
    {
        var github = new FakeGitHub();
        var files = FakeGitHub.FromDisk("README.md");
        files["docs/manual.pdf"] = "%PDF-1.4 pretend"u8.ToArray();
        files["docs/spec v2.docx"] = [0x50, 0x4B, 0x03, 0x04];
        files["plc/Main.st"] = "PROGRAM Main\n"u8.ToArray();
        github.Commit(github.Add("acme", "plant"), "main", files);
        var secret = github.Add("acme", "secret", isPrivate: true);
        github.Commit(secret, "main", new Dictionary<string, byte[]>(files, StringComparer.Ordinal));
        return github;
    }

    [Fact]
    public async Task A_pdf_is_served_inline_with_our_content_type_and_nosniff()
    {
        await using var host = await TestHost.StartAsync(GitHub());

        var response = await host.GetAsync("/api/repos/acme/plant/raw?path=docs%2Fmanual.pdf");

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("%PDF-1.4 pretend", await response.Body());
    }

    [Fact]
    public async Task An_office_document_is_a_download_never_a_page()
    {
        await using var host = await TestHost.StartAsync(GitHub());

        var response = await host.GetAsync("/api/repos/acme/plant/raw?path=" + Uri.EscapeDataString("docs/spec v2.docx"));

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("spec v2.docx", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_document_downloads_as_plain_text_which_nothing_executes()
    {
        await using var host = await TestHost.StartAsync(GitHub());

        var response = await host.GetAsync("/api/repos/acme/plant/raw?path=README.md");

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Theory]
    [InlineData("plc/Main.st")]
    [InlineData("docs/missing.pdf")]
    [InlineData("../docs/manual.pdf")]
    public async Task Anything_off_the_document_allowlist_or_off_the_tree_is_not_found(string path)
    {
        await using var host = await TestHost.StartAsync(GitHub());

        (await host.GetAsync("/api/repos/acme/plant/raw?path=" + Uri.EscapeDataString(path))).AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_raw_route_may_be_framed_by_this_site_and_nothing_else_may_be_framed_at_all()
    {
        // Found by the owner in a real browser (2026-10-16): the blanket X-Frame-Options DENY
        // blocked the viewer's OWN pdf frame, which Chrome draws as "refused to connect" and the
        // built-in browser as a silent white box. The raw route alone answers SAMEORIGIN.
        await using var host = await TestHost.StartAsync(GitHub());

        var raw = await host.GetAsync("/api/repos/acme/plant/raw?path=docs%2Fmanual.pdf");
        Assert.Equal("SAMEORIGIN", raw.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'self'", raw.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        // The clickjacking lock stays on everything beside it — including a repository that
        // happens to be NAMED raw, whose route is one segment shorter.
        foreach (var locked in new[] { "/api/repos/acme/plant", "/api/repos/acme/raw", "/" })
        {
            var response = await host.GetAsync(locked);
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        }
    }

    [Fact]
    public async Task A_private_repositorys_document_is_not_found_anonymously()
    {
        await using var host = await TestHost.StartAsync(GitHub());

        var response = await host.GetAsync("/api/repos/acme/secret/raw?path=docs%2Fmanual.pdf");

        response.AssertStatus(HttpStatusCode.NotFound);
        Assert.DoesNotContain("PDF", await response.Body(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs/Manual.PDF", "application/pdf", true)]
    [InlineData("notes.TXT", "text/plain", false)]
    [InlineData("a.folder.v1/file", null, false)]
    [InlineData("no-extension", null, false)]
    [InlineData("code.st", null, false)]
    public void The_allowlist_decides_by_extension_case_blind_and_never_from_content(string path, string? contentType, bool inline)
    {
        var serve = Documents.DispositionFor(path);

        if (contentType is null)
        {
            Assert.Null(serve);
        }
        else
        {
            Assert.Equal(contentType, serve!.Value.ContentType);
            Assert.Equal(inline, serve.Value.Inline);
        }
    }
}
