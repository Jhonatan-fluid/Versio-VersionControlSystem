using System.Net;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// What one page load costs in GitHub calls and bytes, pinned so it cannot quietly grow back.
/// </summary>
/// <remarks>
/// Measured on 2026-10-11 before the fixes: a page load made 17 GitHub calls (four simultaneous
/// visibility checks, the model loaded twice, the branch resolved per request), two more clicks
/// made 3, and the 33,644-line visualisation's Raw view went out as 7.5 MB uncompressed. After:
/// 9, 1, and about 420 KB (the tree later added catalog reads, once per commit, in parallel).
/// The tests pin "each thing once", so the duplicates cannot return.
/// </remarks>
public sealed class PerformanceTests
{
    private const string Sim = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Simulation.st";
    private const string Sig = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Signals.xml";
    private const string Big = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Live_Visu.vis.xml";

    private static FakeGitHub GitHub()
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        github.Commit(repo, "main", FakeGitHub.FromDisk(".versio/repo.json", "fixtures/projects/MachineWithMembers", "fixtures/projects/RefrigeratorControl"));
        return github;
    }

    [Fact]
    public async Task A_page_load_asks_GitHub_for_each_thing_once()
    {
        var github = GitHub();
        await using var host = await TestHost.StartAsync(github);

        // What the page asks for on load, all at once, as the browser sends it.
        await Task.WhenAll(
            host.GetAsync("/api/repos/acme/plant"),
            host.GetAsync("/api/repos/acme/plant/branches"),
            host.GetAsync("/api/repos/acme/plant/tree?ref=main"),
            host.GetAsync($"/api/repos/acme/plant/view?ref=main&path={Uri.EscapeDataString(Sim)}"));

        // The invariant is "each thing once", not a raw count: the tree grew catalog reads (each
        // project's library, task and device files, fetched in parallel, once per commit) after
        // this test was written with a ceiling of 9, and that ceiling then failed for the right
        // feature. What must never come back is the same thing fetched twice.
        var by = github.Reads.GroupBy(r => r.What.Split(' ')[0]).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, by.GetValueOrDefault("tree"));
        Assert.Equal(1, by.GetValueOrDefault("resolve"));
        Assert.True(by.GetValueOrDefault("repo") <= 2, $"repo x{by.GetValueOrDefault("repo")}");
        var blobs = github.Reads.Where(r => r.What.StartsWith("blob ", StringComparison.Ordinal)).Select(r => r.What).ToList();
        Assert.Equal(blobs.Count, blobs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Clicking_more_objects_costs_only_their_files()
    {
        var github = GitHub();
        await using var host = await TestHost.StartAsync(github);
        await host.GetAsync($"/api/repos/acme/plant/view?ref=main&path={Uri.EscapeDataString(Sim)}");
        github.Reads.Clear();

        await host.GetAsync($"/api/repos/acme/plant/view?ref=main&path={Uri.EscapeDataString(Sig)}");
        await host.GetAsync($"/api/repos/acme/plant/view?ref=main&path={Uri.EscapeDataString(Sim)}&view=raw");

        Assert.All(github.Reads, r => Assert.StartsWith("blob ", r.What, StringComparison.Ordinal));
        Assert.Single(github.Reads);
    }

    [Fact]
    public async Task A_large_view_is_compressed_when_the_browser_accepts_it()
    {
        await using var host = await TestHost.StartAsync(GitHub());
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/repos/acme/plant/view?ref=main&path={Uri.EscapeDataString(Big)}&view=raw");
        request.Headers.Add("Accept-Encoding", "br, gzip");

        var response = await host.Client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Contains(response.Content.Headers.ContentEncoding, e => e is "br" or "gzip");
        Assert.True(bytes.Length < 1024 * 1024, $"{bytes.Length / 1024} KB over the wire");
    }

    [Fact]
    public async Task Built_assets_are_cached_for_a_year_and_the_page_is_always_revalidated()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>Versio</title>");
        }

        await File.WriteAllTextAsync(Path.Combine(root, "assets", "probe-cache.js"), "// built");
        await using var host = await TestHost.StartAsync(GitHub());

        var asset = await host.GetAsync("/assets/probe-cache.js");
        var page = await host.GetAsync("/acme/plant/blob/main/a.st");
        var coreCss = await host.GetAsync("/assets/code.css");

        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.True(page.Headers.CacheControl?.NoCache, page.Headers.CacheControl?.ToString());

        // An unhashed page or script outside /assets keeps its name across deploys: revalidated.
        // The first rule cached these for an hour and a browser kept a stale diagram frame. The
        // frame file itself is real: TestHost installs it (a stub written here once outlived
        // every rebuild, FINDINGS 2026-10-16).
        var frame = await host.GetAsync("/mermaid-frame.html");
        Assert.True(frame.Headers.CacheControl?.NoCache, frame.Headers.CacheControl?.ToString());
        Assert.Equal(TimeSpan.FromMinutes(5), coreCss.Headers.CacheControl?.MaxAge);
    }
}
