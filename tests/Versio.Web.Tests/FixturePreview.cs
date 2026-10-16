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

        // The Documentation tab has one of each kind to look at: the text documents above, a
        // small generated PDF, and an Office download. The PDF is the one worth a look in the
        // browser — it is the embedded case, and it must actually DRAW (a first version without
        // an xref table loaded as a blank white page, which proved only the plumbing).
        files["docs/Plant manual.pdf"] = TinyPdf("Versio: the plant manual");
        files["docs/IO list.xlsx"] = [0x50, 0x4B, 0x03, 0x04];
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

    /// <summary>
    /// One page of Helvetica saying <paramref name="text"/>. The xref offsets are computed, not
    /// hand-counted — PDFium renders a reconstructed file as blank rather than failing loudly,
    /// so a broken table here would quietly show nothing.
    /// </summary>
    private static byte[] TinyPdf(string text)
    {
        var stream = $"BT /F1 18 Tf 24 100 Td ({text}) Tj ET";
        string[] objects =
        [
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 360 144]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            // The newline before endobj is load-bearing: without it "endstreamendobj" is one
            // invalid token, and Chrome's viewer showed its broken-document page (seen by the
            // owner, 2026-10-16). The dictionary objects end in ">>", a delimiter, so they
            // need no separator.
            $"<</Length {stream.Length}>>stream\n{stream}\nendstream\n",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new long[objects.Length + 1];
        for (var i = 1; i <= objects.Length; i++)
        {
            offsets[i] = pdf.Length; // ASCII throughout, so characters are bytes
            pdf.Append(i).Append(" 0 obj").Append(objects[i - 1]).Append("endobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Length; i++)
        {
            pdf.Append(offsets[i].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer<</Root 1 0 R/Size ").Append(objects.Length + 1).Append(">>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
