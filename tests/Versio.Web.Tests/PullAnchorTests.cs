using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Versio.Core.Iec.Ladder;
using Versio.Core.Review;
using Versio.Plc.Codesys;
using Versio.Web.Api;
using Versio.Web.Auth;
using Versio.Web.Review;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Comments on drawn elements (docs/web/PLAN.md, *Pull request review*, slice 3): posted at the
/// element's own lines inside GitHub's hunks, tagged by the server from its own rendering, and
/// found again — or honestly not — when the threads come back.
/// </summary>
public sealed partial class PullAnchorTests
{
    private const string Root = "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/";
    private const string Struct = Root + "Library/Alarm/DUTs/ST_AlarmSettings.st";
    private const string Signals = Root + "Signals.xml";

    private static readonly DateTimeOffset When = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private sealed record World(FakeGitHub GitHub, FakeGitHub.FakeRepo Repo, string Start, string Head, string StructLine);

    /// <summary>
    /// The fixture project; the pull request sets one struct default and deletes Signals' first
    /// network, so every network after it is renumbered.
    /// </summary>
    private static World Build(Func<Dictionary<string, byte[]>, Dictionary<string, byte[]>>? head = null)
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var files = FakeGitHub.FromDisk(".versio/repo.json", "fixtures/projects/RefrigeratorControl");
        var start = github.Commit(repo, "main", files);

        var changed = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        var structText = Encoding.UTF8.GetString(changed[Struct]);
        changed[Struct] = Encoding.UTF8.GetBytes(structText.Replace("Enable\t\t\t\t: BOOL := TRUE;", "Enable\t\t\t\t: BOOL := FALSE;", StringComparison.Ordinal));
        var signals = XDocument.Parse(Encoding.UTF8.GetString(changed[Signals]).TrimStart('﻿'), LoadOptions.PreserveWhitespace);
        signals.Descendants().First(e => e.Name.LocalName == "List2" && (string?)e.Attribute("Name") == "NetworkList").Elements().First().Remove();
        changed[Signals] = Encoding.UTF8.GetBytes(signals.ToString(SaveOptions.DisableFormatting));
        changed = head?.Invoke(changed) ?? changed;
        var headSha = github.Commit(repo, "fix", changed);

        repo.MergeBases[(start, headSha)] = start;
        repo.Pulls.Add((new PullInfo(5, "Alarm default off", "jo", When, "main", "fix", headSha, false), "Why"));
        var line = structText.Replace("\r\n", "\n").Split('\n').ToList().FindIndex(l => l.Contains("Enable", StringComparison.Ordinal)) + 1;
        repo.PullFiles[5] = new PullFileList(
        [
            new PullFile(Struct, "modified", null, $"@@ -{line} +{line} @@\n-\tEnable\t\t\t\t: BOOL := TRUE;\n+\tEnable\t\t\t\t: BOOL := FALSE;"),
            new PullFile(Signals, "modified", null, "@@ -20,3 +20,3 @@\n a\n-b\n+c\n d"),
        ], false);
        return new World(github, repo, start, headSha, line.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static readonly Dictionary<string, string?> WritesOn = new()
    {
        ["Web:Writes:Enabled"] = "true",
        ["Web:Writes:Repositories:0"] = "acme/plant",
        ["Web:Writes:SingleInstance"] = "true",
    };

    private static async Task<(TestHost Host, FakeWriter Writer, string Alice, string Token)> StartAsync(World w)
    {
        var writer = new FakeWriter();
        var host = await TestHost.StartAsync(w.GitHub, WritesOn, s => s.AddSingleton<IRepoWriter>(writer));
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6));
        var alice = $"{AccessResolver.SessionCookie}={session.Id}";
        var snapshot = await host.GetAsync("/api/repos/acme/plant/pulls/5/snapshot", alice);
        snapshot.AssertStatus(HttpStatusCode.OK);
        return (host, writer, alice, JsonDocument.Parse(await snapshot.Body()).RootElement.GetProperty("token").GetString()!);
    }

    private static Dictionary<string, string> Headers(string token) => new()
    {
        [AuthEndpoints.CsrfHeader] = "1",
        [PullEndpoints.SnapshotHeader] = token,
        ["Sec-Fetch-Site"] = "same-origin",
    };

    private static string Key() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

    private static async Task<JsonElement> View(TestHost host, string token, string alice, string path, string? from = null)
    {
        var url = $"/api/repos/acme/plant/pulls/5/files/view?path={Uri.EscapeDataString(path)}" + (from is null ? "" : $"&from={Uri.EscapeDataString(from)}");
        var response = await host.GetAsync(url, alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token });
        response.AssertStatus(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Body()).RootElement;
    }

    [GeneratedRegex("data-path=\"([^\"]*)\" data-change=\"[a-z]+\" data-anchors=\"([^\"]*)\"")]
    private static partial Regex RowAnchors();

    /// <summary>The new-side anchor id of a member row, as the drawing names it.</summary>
    private static string RowDom(JsonElement view, string member) =>
        RowAnchors().Matches(view.GetProperty("view").GetProperty("html").GetString()!)
            .Single(m => m.Groups[1].Value == member).Groups[2].Value.Split(' ').Single(d => d.StartsWith("r-new-", StringComparison.Ordinal));

    private static Task<HttpResponseMessage> Comment(TestHost host, string alice, string token, string path, string dom, string body = "Why off?", string? from = null) =>
        host.SendAsync(HttpMethod.Post, "/api/repos/acme/plant/pulls/5/anchor-comments", alice, Headers(token),
            JsonSerializer.Serialize(new { opKey = Key(), body, path, from, dom }));

    // ---- posting --------------------------------------------------------------------------------

    /// <summary>
    /// A comment on a member row goes on that row's own line, which GitHub's hunk shows, on the
    /// snapshot's head — and the body carries the anchor the SERVER read from its own rendering.
    /// </summary>
    [Fact]
    public async Task A_comment_on_a_changed_row_goes_on_its_own_line_tagged_by_the_server()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        var dom = RowDom(await View(host, token, alice, Struct), "Enable");

        var response = await Comment(host, alice, token, Struct, dom);

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal($"{w.Head} {Struct} RIGHT -{w.StructLine}", Assert.Single(writer.Writes).Detail);
        var tag = ReviewTags.ReadAnchor(Assert.Single(writer.Comments.Values).Body)!;
        Assert.Equal(("row", Struct, ReviewSide.New, w.Head, "Enable"), (tag.Kind, tag.SourcePath, tag.Side, tag.Revision, tag.Key));
        Assert.Equal("ST_AlarmSettings", tag.Container);
    }

    /// <summary>
    /// A row GitHub's diff does not show (unchanged) has no line GitHub takes: the comment goes on the
    /// whole file, never on a nearby line, and the tag still says which row.
    /// </summary>
    [Fact]
    public async Task A_comment_on_an_unchanged_row_goes_on_the_file_and_still_names_the_row()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        var view = await View(host, token, alice, Struct);
        var member = RowAnchors().Matches(view.GetProperty("view").GetProperty("html").GetString()!).Select(m => m.Groups[1].Value).First(p => p != "Enable");

        (await Comment(host, alice, token, Struct, RowDom(view, member))).AssertStatus(HttpStatusCode.OK);

        Assert.Equal($"{w.Head} {Struct} RIGHT -", Assert.Single(writer.Writes).Detail);
        Assert.Equal(member, ReviewTags.ReadAnchor(writer.Comments.Values.Single().Body)!.Key);
    }

    [Theory]
    [InlineData("r-new-9999", HttpStatusCode.UnprocessableEntity)]
    [InlineData("<script>", HttpStatusCode.BadRequest)]
    [InlineData("n-new-1 r-new-2", HttpStatusCode.BadRequest)]
    public async Task An_element_the_drawing_does_not_have_is_refused_and_nothing_is_posted(string dom, HttpStatusCode status)
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Comment(host, alice, token, Struct, dom)).AssertStatus(status);

        Assert.Empty(writer.Writes);
    }

    // ---- placing --------------------------------------------------------------------------------

    private static ReviewThread Thread(string id, string path, string body, int? line, string side = "RIGHT", bool outdated = false) =>
        new(id, path, false, outdated, line, null, side, line, "LINE", [new ReviewComment("C_" + id, "alice", body, When, null, null)], false);

    private static JsonElement Placement(JsonElement view, string thread) =>
        view.GetProperty("placements").EnumerateArray().Single(p => p.GetProperty("threadId").GetString() == thread);

    /// <summary>The round trip: what was posted, read back as GitHub returns it, lands on the same row.</summary>
    [Fact]
    public async Task A_posted_comment_read_back_lands_on_the_row_it_was_written_on()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        var dom = RowDom(await View(host, token, alice, Struct), "Enable");
        await Comment(host, alice, token, Struct, dom);
        w.Repo.Threads[5] = new ReviewThreadList([Thread("T1", Struct, writer.Comments.Values.Single().Body, int.Parse(w.StructLine, System.Globalization.CultureInfo.InvariantCulture))], false);

        var placed = Placement(await View(host, token, alice, Struct), "T1");

        Assert.Equal(("exact", dom), (placed.GetProperty("outcome").GetString(), placed.GetProperty("dom").GetString()));
    }

    /// <summary>A comment written on github.com, with no tag, is placed on the row whose lines hold its line.</summary>
    [Fact]
    public async Task A_github_native_line_comment_lands_on_the_row_holding_its_line()
    {
        var w = Build();
        w.Repo.Threads[5] = new ReviewThreadList([Thread("T1", Struct, "Written on GitHub.", int.Parse(Build().StructLine, System.Globalization.CultureInfo.InvariantCulture))], false);
        var (host, _, alice, token) = await StartAsync(w);
        await using var __ = host;
        var view = await View(host, token, alice, Struct);

        var placed = Placement(view, "T1");

        Assert.Equal(("line", RowDom(view, "Enable")), (placed.GetProperty("outcome").GetString(), placed.GetProperty("dom").GetString()));
    }

    /// <summary>
    /// A tag never silently beats a line: when GitHub's current line for the thread is outside the
    /// element the tag names, one of them is wrong, and the thread is listed rather than placed.
    /// </summary>
    [Fact]
    public async Task A_tag_and_a_line_that_disagree_place_nothing()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        await Comment(host, alice, token, Struct, RowDom(await View(host, token, alice, Struct), "Enable"));
        w.Repo.Threads[5] = new ReviewThreadList([Thread("T1", Struct, writer.Comments.Values.Single().Body, 1)], false);

        var placed = Placement(await View(host, token, alice, Struct), "T1");

        Assert.Equal(("unplaced", "disagrees"), (placed.GetProperty("outcome").GetString(), placed.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task An_outdated_comment_without_a_tag_is_listed_not_guessed()
    {
        var w = Build();
        w.Repo.Threads[5] = new ReviewThreadList([Thread("T1", Struct, "Old remark.", null, outdated: true)], false);
        var (host, _, alice, token) = await StartAsync(w);
        await using var __ = host;

        var placed = Placement(await View(host, token, alice, Struct), "T1");

        Assert.Equal(("unplaced", "outdated"), (placed.GetProperty("outcome").GetString(), placed.GetProperty("reason").GetString()));
    }

    /// <summary>
    /// A network comment written on an older revision, after the network before it was deleted: its
    /// number changed, its content did not, so it is found as Moved on the network now holding that
    /// content — and two networks with that content would make it ambiguous instead.
    /// </summary>
    [Fact]
    public async Task A_renumbered_network_is_found_as_moved_by_its_content()
    {
        var w = Build();
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;
        var original = CodesysProjectFormat.Instance.TryParseLadder(Signals, Encoding.UTF8.GetString(w.Repo.Commits[w.Start][Signals]))!;
        var second = LadderHtml.Anchors(LadderDiff.Compare(null, original.Body), Signals).Single(a => a.Dom == "n-new-2");
        var tag = new SemanticAnchor("net", Signals, ReviewSide.New, "an-earlier-head", second.Container, second.Key, second.Fingerprint);
        w.Repo.Threads[5] = new ReviewThreadList([Thread("T1", Signals, ReviewTags.WithAnchor("This rung?", Key(), tag), null, outdated: true)], false);

        var view = await View(host, token, alice, Signals);
        var placed = Placement(view, "T1");

        Assert.Equal(("moved", "n-new-1"), (placed.GetProperty("outcome").GetString(), placed.GetProperty("dom").GetString()));
        Assert.Contains("data-anchors=\"n-new-1", view.GetProperty("view").GetProperty("html").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rename GitHub reported is drawn as the object compared with itself: the rows that did not
    /// change read unchanged, rather than the whole file reading as added.
    /// </summary>
    [Fact]
    public async Task A_renamed_file_is_compared_with_its_old_path_when_told_it()
    {
        const string renamed = Root + "Library/Alarm/DUTs/ST_AlarmSettings2.st";
        var w = Build(files =>
        {
            files[renamed] = files[Struct];
            files.Remove(Struct);
            return files;
        });
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var cold = (await View(host, token, alice, renamed)).GetProperty("view").GetProperty("html").GetString()!;
        var told = (await View(host, token, alice, renamed, from: Struct)).GetProperty("view").GetProperty("html").GetString()!;

        Assert.DoesNotContain("data-change=\"none\"", cold, StringComparison.Ordinal);
        Assert.Contains("data-change=\"none\"", told, StringComparison.Ordinal);
        Assert.Contains("data-change=\"modified\"", told, StringComparison.Ordinal);
    }
    // ---- coverage (C4) ---------------------------------------------------------------------------

    /// <summary>
    /// The struct's one change is a value the table names on a row it marks modified: complete. The
    /// ladder has no field evidence yet, so its drawing is partial and its changed lines are named.
    /// </summary>
    [Fact]
    public async Task A_drawing_is_complete_only_with_evidence_and_partial_says_where()
    {
        var w = Build();
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var table = await View(host, token, alice, Struct);
        var ladder = await View(host, token, alice, Signals);

        Assert.Equal("complete", table.GetProperty("coverage").GetString());
        Assert.Equal(0, table.GetProperty("gaps").GetArrayLength());
        Assert.Equal("partial", ladder.GetProperty("coverage").GetString());
        var gaps = ladder.GetProperty("gaps").EnumerateArray().ToList();
        Assert.NotEmpty(gaps);
        // Signals draws an interface table from its own XML: table evidence exists for the file, but
        // never vouches for an XML line, so every changed line is "not shown" (and the ladder adds none).
        Assert.All(gaps, g => Assert.Equal((Signals, "not-shown"), (g.GetProperty("path").GetString(), g.GetProperty("reason").GetString())));
    }

    /// <summary>
    /// A layer that changed but is not part of the drawing at all — the GVL's XML beside the .st
    /// being looked at — is a gap, "not drawn". A drawing is complete about the object, not only about
    /// the file it happens to draw.
    /// </summary>
    [Fact]
    public async Task A_changed_layer_the_drawing_does_not_draw_keeps_it_partial()
    {
        const string gvl = Root + "Glob_Var.gvl.st";
        const string xml = Root + "Glob_Var.gvl.xml";
        var w = Build(files =>
        {
            files[xml] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[xml]) + "\n<!-- touched -->\n");
            return files;
        });
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var view = await View(host, token, alice, gvl);

        Assert.Equal("partial", view.GetProperty("coverage").GetString());
        Assert.Contains(view.GetProperty("gaps").EnumerateArray(), g => g.GetProperty("path").GetString() == xml && g.GetProperty("reason").GetString() == "not-drawn");
    }

    [Fact]
    public async Task The_summary_says_per_file_whether_its_drawing_covers_every_change()
    {
        var w = Build();
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var response = await host.GetAsync("/api/repos/acme/plant/pulls/5/coverage", alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token });

        response.AssertStatus(HttpStatusCode.OK);
        var files = JsonDocument.Parse(await response.Body()).RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("coverage").GetString());
        Assert.Equal("complete", files[Struct]);
        Assert.Equal("partial", files[Signals]);
    }
}
