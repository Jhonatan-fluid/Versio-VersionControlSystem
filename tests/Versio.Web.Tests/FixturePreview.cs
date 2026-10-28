using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Versio.Core.Iec.Ladder;
using Versio.Core.Review;
using Versio.Plc.Codesys;
using Versio.Web.Auth;
using Versio.Web.Review;
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
/// /acme/plant/compare/main...next has something to show, and /acme/plant/pull/1 is a whole
/// review of it. It serves for 25 minutes; stop the run to end it sooner.
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
        // The TIA export is the one with LAD and FBD blocks, so it is the one that shows the
        // ladder renderer. It is Git LFS: a clone without the content serves pointer text
        // instead, and the tree's "could not be read" notice says so rather than anything failing.
        var files = FakeGitHub.FromDisk(".versio/repo.json", "README.md", "docs/web/PLAN.md", "docs/dev.md",
            "fixtures/projects/MachineWithMembers", "fixtures/projects/RefrigeratorControl", "fixtures/projects/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText",
            "fixtures/projects/CleaningStation_V20_V21/CleaningStation_V20_V21");

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
        // Signals loses its first network, so every network after it is renumbered: the case review
        // anchors exist for (a comment on network 2 is found again on network 1).
        var signalsPath = "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Signals.xml";
        var signals = XDocument.Parse(Encoding.UTF8.GetString(changed[signalsPath]).TrimStart('\uFEFF'), LoadOptions.PreserveWhitespace);
        signals.Descendants().First(e => e.Name.LocalName == "List2" && (string?)e.Attribute("Name") == "NetworkList").Elements().First().Remove();
        changed[signalsPath] = Encoding.UTF8.GetBytes(signals.ToString(SaveOptions.DisableFormatting));
        var next = github.Commit(repo, "next", changed);

        // The pulls and releases tabs have something to look at: next as an open pull request —
        // its "What changed" lands on the same two files /compare/main...next shows — and two
        // releases, one of them a pre-release with no notes.
        repo.Pulls.Add((new PullInfo(1, "Lower the simulated cooling step", "sam", DateTimeOffset.Now.AddHours(-3), "main", "cooling-step", next, Draft: false),
            "## Why\n\nThe simulation cooled twice as fast as the plant, so every soak test passed that should not have.\n"));
        // The review page (/acme/plant/pull/1) has a whole review to look at: the merge-base,
        // GitHub's patches, a thread on each side of the changed simulation line (placed under
        // their rows on Raw), one outdated thread (listed, not placed), a hostile comment that
        // must draw as text, checks in three states and a conversation.
        var simulation = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Simulation.st";
        var alarm = "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library/Alarm/DUTs/ST_AlarmSettings.st";
        repo.MergeBases[(repo.Refs["main"], next)] = repo.Refs["main"];
        // The commit page (Step 8) needs a parent to compare from and a message to show.
        repo.Parents[next] = [repo.Refs["main"]];
        repo.CommitMessages[next] = "Lower the simulated cooling step\n\nThe simulation cooled twice as fast as the plant, so every soak test passed that should not have.\n";

        // A third commit, on branch `history` above `next`, that edits only the native XML layer
        // of a two-layer GVL: the case the commit page's layer chips exist for (one section for
        // Glob_Var, the .xml drawn, the .st offered), and the case an object's history used to
        // miss. /acme/plant/commit/<its sha> and /acme/plant/commits/history/<Glob_Var.gvl.st>.
        // Its own branch, so pull request 1 and /compare/main...next stay what they were.
        var globVarXml = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Glob_Var.gvl.xml";
        var layered = new Dictionary<string, byte[]>(changed, StringComparer.Ordinal)
        {
            [globVarXml] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed[globVarXml]).Replace("TempActual: REAL:=8.0;", "TempActual: REAL:=9.0;", StringComparison.Ordinal)),
        };
        var history = github.Commit(repo, "history", layered);
        repo.Parents[history] = [next];
        repo.CommitMessages[history] = "Start the simulation one degree warmer\n\nOnly the GVL's native XML carries the initial value here.\n";
        repo.PullFiles[1] = new PullFileList(
        [
            new PullFile(alarm, "modified", null, "@@ -9 +9 @@\n-\tEnable\t\t\t\t: BOOL := TRUE;\n+\tEnable\t\t\t\t: BOOL := FALSE;"),
            // Shaped like GitHub's: three lines of context each side, so lines 19-25 take comments.
            new PullFile(simulation, "modified", null, "@@ -19,7 +19,7 @@\n a\n b\n c\n-x\n+y\n d\n e\n f"),
            new PullFile(signalsPath, "modified", null, "@@ -25,4 +25,1 @@\n a\n-b\n-c\n-d"),
        ], false);
        var when = DateTimeOffset.Now.AddHours(-2);
        repo.Threads[1] = new ReviewThreadList(
        [
            new ReviewThread("T-new", simulation, false, false, 22, null, "RIGHT", 22, "LINE",
            [
                new ReviewComment("C1", "sam", "Half the step: is **0.2** right for the plant's cooling, or is that the soak rig?", when, next, next, CanEdit: true, CanDelete: true)
                {
                    ReviewId = "PRR_LEE", DiffHunk = "@@ -19,7 +19,7 @@\n a\n b\n c\n-x\n+y", AuthorAssociation = "OWNER", Reactions = [new ReactionGroup("HEART", 1, false)],
                },
                new ReviewComment("C2", "jo", "Plant data: 0.2/s. The rig was 0.1.\n\n<script>alert('this must draw as text')</script>", when.AddMinutes(9), next, next) { ReviewId = "PRR_LEE", EditedAt = when.AddMinutes(12) },
                // A suggested change (Step 5): drawn as a diff under the comment, with Apply / Add to batch under the merge tier.
                new ReviewComment("C2s", "lee", "Then say so in the code:\n```suggestion\n\t\t\tGlob_Var.TempActual := Glob_Var.TempActual - 0.2; (* plant data *)\n```", when.AddMinutes(14), next, next) { ReviewId = "PRR_LEE", AuthorAssociation = "COLLABORATOR" },
            ], false, CanReply: true, CanResolve: true),
            new ReviewThread("T-old", simulation, true, false, 22, null, "LEFT", 22, "LINE",
                [new ReviewComment("C3", "sam", "This old value is the bug.", when, next, next, CanEdit: true, CanDelete: true)], false, CanReply: true, CanUnresolve: true),
            new ReviewThread("T-gone", alarm, false, true, null, null, "RIGHT", 4, "LINE",
                // Outdated: GitHub keeps the hunk it was written on, which the page shows (Step 3b).
                [new ReviewComment("C4", "sam", "An earlier remark on a line that has since moved.", when.AddDays(-1), null, next) { DiffHunk = "@@ -2,4 +2,4 @@\n TYPE ST_AlarmSettings :\n STRUCT\n-\tbEnabled : BOOL;\n+\tbEnabled : BOOL := FALSE;" }], false),
            // Written on github.com on the struct's changed line, no tag: placed on the row holding it.
            new ReviewThread("T-row", alarm, false, false, 9, null, "RIGHT", 9, "LINE",
                [new ReviewComment("C5", "lee", "Off by default means a fresh controller raises nothing. Intended?", when.AddMinutes(30), next, next)], false, CanReply: true, CanResolve: true),
            // Written in Versio on network 2 before network 1 was deleted: found again, as moved, on network 1.
            new ReviewThread("T-net", signalsPath, false, true, null, null, "RIGHT", 40, "LINE",
                [new ReviewComment("C6", "sam", ReviewTags.WithAnchor("Does this rung still need the second timer?", $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-previewpreviewpreview", NetworkTwo(files[signalsPath], signalsPath)), when.AddHours(-1), null, next)], false, CanReply: true, CanResolve: true),
        ], false);
        repo.Checks[next] =
        [
            new CheckInfo("build", "completed", "success", "https://github.com/acme/plant/actions/runs/1", "check-run") { Id = 1, App = "GitHub Actions", Suite = 10, StartedAt = when.AddMinutes(3), CompletedAt = when.AddMinutes(7), Title = "Build passed", Summary = "All **12** projects built." },
            new CheckInfo("plc-compile", "completed", "failure", "https://github.com/acme/plant/actions/runs/2", "check-run") { Id = 2, App = "GitHub Actions", Suite = 10, StartedAt = when.AddMinutes(3), CompletedAt = when.AddMinutes(9), Title = "2 errors", Summary = "`PLC_PRG` line 12: unknown identifier `tSetpoint`.", AnnotationCount = 2 },
            new CheckInfo("soak-test", "in_progress", null, null, "check-run") { Id = 3, App = "Soak rig", Suite = 11, StartedAt = when.AddMinutes(10) },
            new CheckInfo("ci/jenkins", "completed", "success", "https://jenkins.example/job/plant/42", "status") { App = "jenkins-bot", Description = "Build #42 passed", CompletedAt = when.AddMinutes(8) },
        ];
        // The compile run's two annotations (Step 3b): listed under the run on Checks, drawn on the
        // lines on Files changed — one on the changed program's new line, one as a warning.
        repo.Annotations[2] =
        [
            new CheckAnnotation(simulation, 22, 22, "failure", "C0046: identifier 'tSetpoint' not defined", "Compile error"),
            new CheckAnnotation(simulation, 3, 3, "warning", "C0035: variable 'rStep' is declared but never used", null),
        ];
        // Two comments on the head commit (Step 3b): one on the commit, one GitHub placed on a line.
        repo.CommitComments[next] =
        [
            new CommitComment("CC_1", "lee", null, "Is the cooling step right for the **small** plant too?", when.AddMinutes(12), null, null, null),
            new CommitComment("CC_2", "sam", null, "Yes: the simulation was the only thing off.", when.AddMinutes(20), simulation, 22, null),
        ];
        // The conversation GitHub would show: the push, the label and reviewer events, a comment
        // with reactions and an edit mark, a review whose thread the page lists under it (its
        // comments name the review by id), a cross-reference, a dismissal.
        repo.Timelines[1] = new Timeline(
        [
            new TimelineItem("commit", "sam", when, "Lower the simulated cooling step", null, next) { Verified = true },
            new TimelineItem("labeled", "sam", when.AddMinutes(1), null, null, null) { Target = "plc", TargetColor = "1a7f37" },
            new TimelineItem("labeled", "sam", when.AddMinutes(1), null, null, null) { Target = "needs-soak", TargetColor = "9a6700" },
            new TimelineItem("review-requested", "sam", when.AddMinutes(2), null, null, null) { Target = "lee" },
            new TimelineItem("review-requested", "sam", when.AddMinutes(2), null, null, null) { Target = "controls" },
            new TimelineItem("renamed", "sam", when.AddMinutes(5), null, null, null) { Previous = "Lower the cooling step", Target = "Lower the simulated cooling step" },
            new TimelineItem("comment", "jo", when.AddMinutes(20), "Soak tests went green with this. **Please check** the interlock still trips.", null, null, "IC_1", CanEdit: true, CanDelete: true)
            {
                AuthorAssociation = "MEMBER", EditedAt = when.AddMinutes(25), Reactions = [new ReactionGroup("THUMBS_UP", 2, false), new ReactionGroup("EYES", 1, true)],
            },
            new TimelineItem("cross-referenced", "jo", when.AddMinutes(30), null, null, null) { Reference = new TimelineReference(7, "Soak rig cools too fast", "acme/plant", null, false, "OPEN"), Message = "closes" },
            new TimelineItem("review", "lee", when.AddMinutes(40), "The interlock reads the old value once more before the step lands.", "CHANGES_REQUESTED", next, "PRR_LEE") { AuthorAssociation = "COLLABORATOR" },
            new TimelineItem("review-dismissed", "sam", when.AddMinutes(50), null, null, null) { Target = "lee", Message = "Addressed in the second commit." },
        ], false);
        // What GitHub's header counts and marks: the diff's size, and the one check branch protection requires.
        repo.PullHeader[1] = (4, 4, 3, ["build"]);
        repo.PullCommits[1] = new PullCommitList([new PullCommit(next, "Lower the simulated cooling step", "sam", when) { AuthorLogin = "sam", Body = "The simulation cooled twice as fast as the plant.\n\nMeasured on the soak rig, 2026-10-20.", Verified = true }], false);
        repo.PullPeople[1] = ([new PullLabel("plc", "1a7f37"), new PullLabel("needs-soak", "9a6700")], [new PullReviewer("lee", "CHANGES_REQUESTED"), new PullReviewer("controls", "REQUESTED")]);
        // Slice 5: labels to put on and people who can be assigned. A new pull request can be
        // opened from "next" into "main" (the fixtures carry no template, so its body starts empty).
        repo.Labels.AddRange([new PullLabel("plc", "1a7f37"), new PullLabel("needs-soak", "9a6700"), new PullLabel("safety", "d1242f")]);
        repo.Assignable.UnionWith(["sam", "jo", "lee"]);
        repo.Assignees[1] = ["sam"];
        github.PreviewGraphWithoutToken = true;

        repo.Releases.Add((new ReleaseInfo("v1.1", "Cooling fixes", "sam", DateTimeOffset.Now.AddDays(-2), Prerelease: false),
            "## Changed\n\n- The alarm defaults\n- The simulation step\n"));
        repo.Releases.Add((new ReleaseInfo("v1.2-rc1", "Next cut", "sam", DateTimeOffset.Now.AddDays(-1), Prerelease: true), ""));

        // 5199 unless VERSIO_WEB_PREVIEW_PORT says otherwise: a second preview beside another
        // session's (the same reason PullPreview takes one, 2026-10-23).
        var port = Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW_PORT") is { Length: > 0 } chosen ? chosen : "5199";
        var settings = new Dictionary<string, string?> { ["urls"] = $"http://127.0.0.1:{port}" };

        // VERSIO_WEB_PREVIEW_SCOPED=1 besides: the server scoped to the fixtures repository, to
        // look at the home page's repository list and the off-list refusal ("The product shape"
        // in PLAN.md). Without it the server is open, and the home page is the open-a-repo form.
        if (Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW_SCOPED") == "1")
        {
            settings["Web:Repositories:0"] = "acme/plant";
        }

        // VERSIO_WEB_PREVIEW_WRITES=1 besides: writes on for the fixtures repository, a fake GitHub
        // writer, and a signed-in session for "sam" whose id is written to
        // %TEMP%/versio-web-preview-session.txt — set it as the __Host-versio cookie in the browser
        // to look at the comment boxes, replies and resolve buttons. Posts land in the fake
        // writer, so a new comment does not appear in the threads afterwards; the boxes' outcomes do.
        var writes = Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW_WRITES") == "1";
        // The writer keeps pending reviews and viewed marks in the repository it is given, which the
        // reads then serve back: without it, "Start a review" in the preview was a 500 (2026-10-19).
        var writer = new FakeWriter { Repo = repo };
        writer.Threads["T-new"] = ("acme/plant", 1, false);
        writer.Threads["T-old"] = ("acme/plant", 1, true);
        if (writes)
        {
            settings["Web:Writes:Enabled"] = "true";
            settings["Web:Writes:Repositories:0"] = "acme/plant";
            settings["Web:Writes:SingleInstance"] = "true";
            // The merge tier too (slice 6), so the merge box can be looked at; merges land in the fake.
            settings["Web:Writes:Merge"] = "true";
        }

        await using var host = await TestHost.StartAsync(github, settings, writes ? s => s.AddSingleton<IRepoWriter>(writer) : null);
        if (writes)
        {
            var sessions = host.Services.GetRequiredService<SessionStore>();
            var session = await sessions.CreateAsync(7, "sam", "sam", "", "preview-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(1));
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "versio-web-preview-session.txt"), session.Id);
        }

        // 25 minutes unless VERSIO_WEB_PREVIEW_MINUTES says otherwise. The owner's first look at the
        // page (2026-10-24) outlived two 25-minute previews, and every restart minted a new signed-in
        // session, so the cookie pasted from the file above went stale under them and the page fell
        // silently to read-only: no comment box, no reply buttons, nothing saying why. A day-long
        // preview keeps one session for one look; the session itself is good for eight hours.
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW_MINUTES"), out var m) && m > 0 ? m : 25;
        await Task.Delay(TimeSpan.FromMinutes(minutes));
    }

    /// <summary>The anchor a comment written on Signals' network 2, at the older commit, would carry.</summary>
    private static SemanticAnchor NetworkTwo(byte[] signals, string path)
    {
        var body = CodesysProjectFormat.Instance.TryParseLadder(path, Encoding.UTF8.GetString(signals))!.Body;
        var two = LadderHtml.Anchors(LadderDiff.Compare(null, body), path).Single(a => a.Dom == "n-new-2");
        return new SemanticAnchor("net", path, ReviewSide.New, "an-earlier-head", two.Container, two.Key, two.Fingerprint);
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
