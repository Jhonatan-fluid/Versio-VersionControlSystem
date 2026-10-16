using System.Text;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Not a test: the way to LOOK at the web viewer against this repository's fixtures, with no
/// GitHub and no App. Does nothing unless VERSIO_WEB_PREVIEW=1.
/// </summary>
/// <remarks>
/// <para>
/// Every hosting defect in FINDINGS 2026-10-11 (links to files 404ing, then the page's own assets
/// 404ing) was invisible to the green suite and obvious in a browser within a minute. AGENTS.md's
/// "Run the app" applies here too, and this is how: build the page once with
/// <c>dotnet build src/Versio.Web -p:BuildWeb=true</c>, then
/// </para>
/// <code>
/// VERSIO_WEB_PREVIEW=1 dotnet test tests/Versio.Web.Tests --filter FixturePreview
/// </code>
/// <para>
/// and open http://127.0.0.1:5199/acme/plant. Branch <c>main</c> is the fixtures as committed;
/// <c>next</c> changes one struct default and one program line, so
/// /acme/plant/compare/main...next has something to show. It serves for 25 minutes; stop the
/// run to end it sooner.
/// </para>
/// </remarks>
public sealed class FixturePreview
{
    [Fact]
    public async Task Serve_the_fixtures_on_port_5199_when_asked()
    {
        if (Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW") != "1")
        {
            return;
        }

        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var files = FakeGitHub.FromDisk(".versio/repo.json", "README.md", "docs/web/PLAN.md", "docs/dev.md",
            "fixtures/projects/MachineWithMembers", "fixtures/projects/RefrigeratorControl", "fixtures/projects/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText");
        github.Commit(repo, "main", files);

        var changed = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        void Edit(string path, string from, string to) =>
            changed[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed[path]).Replace(from, to, StringComparison.Ordinal));
        Edit("fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library/Alarm/DUTs/ST_AlarmSettings.st",
            "Enable				: BOOL := TRUE;", "Enable				: BOOL := FALSE;");
        Edit("fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Simulation.st",
            "Glob_Var.TempActual-0.1;", "Glob_Var.TempActual-0.2;");
        var next = github.Commit(repo, "next", changed);

        // The pulls and releases tabs have something to look at: next as an open pull request —
        // its "What changed" lands on the same two files /compare/main...next shows — and two
        // releases, one of them a pre-release with no notes.
        repo.Pulls.Add((new PullInfo(1, "Lower the simulated cooling step", "sam", DateTimeOffset.Now.AddHours(-3), "main", "cooling-step", next, Draft: false),
            "## Why\n\nThe simulation cooled twice as fast as the plant, so every soak test passed that should not have.\n"));
        repo.Releases.Add((new ReleaseInfo("v1.1", "Cooling fixes", "sam", DateTimeOffset.Now.AddDays(-2), Prerelease: false),
            "## Changed\n\n- The alarm defaults\n- The simulation step\n"));
        repo.Releases.Add((new ReleaseInfo("v1.2-rc1", "Next cut", "sam", DateTimeOffset.Now.AddDays(-1), Prerelease: true), ""));

        var settings = new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:5199" };

        // VERSIO_WEB_PREVIEW_SCOPED=1 besides: the server scoped to the fixtures repository, to
        // look at the home page's repository list and the off-list refusal ("The product shape"
        // in PLAN.md). Without it the server is open, and the home page is the open-a-repo form.
        if (Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW_SCOPED") == "1")
        {
            settings["Web:Repositories:0"] = "acme/plant";
        }

        await using var host = await TestHost.StartAsync(github, settings);
        await Task.Delay(TimeSpan.FromMinutes(25));
    }
}
