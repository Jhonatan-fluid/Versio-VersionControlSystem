using System.Net;
using System.Text.Json;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Links between structures (docs/web/FINISH.md, Step 3c): an identifier in drawn source carries
/// the file of the object it names in the same project; "used by" lists the objects whose source
/// names this one, at this commit, and says when it stopped reading.
/// </summary>
public sealed class LinksTests
{
    private const string Simulation = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Simulation.st";
    private const string Pump = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Pumps/FB_Pump.st";
    private const string GlobVar = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Glob_Var.gvl.st";

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static FakeGitHub Build()
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        github.Commit(repo, "main", FakeGitHub.FromDisk(".versio/repo.json", "fixtures/projects/MachineWithMembers"));
        return github;
    }

    [Fact]
    public async Task An_identifier_in_drawn_source_links_to_the_object_it_names_and_the_objects_own_name_does_not()
    {
        await using var host = await TestHost.StartAsync(Build());

        var rendered = Json(await (await host.GetAsync($"/api/repos/acme/plant/view?ref=main&path={Uri.EscapeDataString(Simulation)}")).Body());
        var raw = Json(await (await host.GetAsync($"/api/repos/acme/plant/view?ref=main&path={Uri.EscapeDataString(Simulation)}&view=raw")).Body());

        var html = rendered.GetProperty("html").GetString()!;
        Assert.Contains($"data-link=\"{GlobVar}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"data-link=\"{Simulation}\"", html, StringComparison.Ordinal);
        // Raw is the file as itself: no links, as before.
        Assert.DoesNotContain("data-link", raw.GetProperty("html").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Used_by_lists_the_objects_whose_source_names_this_one_and_nothing_for_a_file_no_object_owns()
    {
        await using var host = await TestHost.StartAsync(Build());

        var usages = await host.GetAsync($"/api/repos/acme/plant/usages?ref=main&path={Uri.EscapeDataString(GlobVar)}");
        var none = await host.GetAsync("/api/repos/acme/plant/usages?ref=main&path=.versio/repo.json");
        var odd = await host.GetAsync("/api/repos/acme/plant/usages?ref=main&path=../x");

        usages.AssertStatus(HttpStatusCode.OK);
        var body = Json(await usages.Body());
        Assert.Equal("Glob_Var", body.GetProperty("name").GetString());
        var users = body.GetProperty("usedBy").EnumerateArray().Select(u => (u.GetProperty("name").GetString(), u.GetProperty("path").GetString())).ToList();
        Assert.Contains(("Simulation", Simulation), users);
        // FB_Pump's source never names the GVL; the GVL is not its own user.
        Assert.DoesNotContain(("FB_Pump", Pump), users);
        Assert.DoesNotContain(users, u => u.Item1 == "Glob_Var");
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.True(body.GetProperty("searched").GetInt32() > 2);
        none.AssertStatus(HttpStatusCode.NotFound);
        odd.AssertStatus(HttpStatusCode.NotFound);
    }
}
