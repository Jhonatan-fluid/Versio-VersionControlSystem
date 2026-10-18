using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Versio.Core.Abstractions;
using Versio.Web.Api;
using Versio.Web.Auth;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Pull request review, read side (docs/web/PLAN.md, *Pull request review*, C1): one revision,
/// sealed, re-authorised on every use, and a file inventory that never shrinks to what GitHub
/// happened to patch.
/// </summary>
public sealed class PullReviewTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// main was at <c>start</c> when the branch was cut and has moved on since; the pull request's
    /// head adds, changes and renames files. The merge-base is <c>start</c>, never main's tip.
    /// </summary>
    private sealed class World
    {
        public required FakeGitHub GitHub { get; init; }

        public required FakeGitHub.FakeRepo Repo { get; init; }

        public required string Start { get; init; }

        public required string MainTip { get; init; }

        public required string Head { get; init; }

        public void Open(string head) =>
            Repo.Pulls[0] = (new PullInfo(12, "Fix interlock", "jo", When, "main", "fix", head, false), "## Why\n\nThe delay was wrong.");
    }

    private static World Build(bool isPrivate = false, Dictionary<string, byte[]>? extraHeadFiles = null)
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant", isPrivate);
        repo.Readers.Add("alice-token");
        var start = github.Commit(repo, "start", new Dictionary<string, byte[]>
        {
            ["src/Pump.st"] = B("PROGRAM Pump\nVAR\n  run : BOOL;\nEND_VAR\n"),
            ["src/Old.st"] = B("PROGRAM Old\nEND_PROGRAM\n"),
            ["src/Same.st"] = B("PROGRAM Same\nEND_PROGRAM\n"),
        });

        // main moves on after the branch was cut: a file the pull request never touches.
        var mainTip = github.Commit(repo, "main", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal)
        {
            ["src/Later.st"] = B("PROGRAM Later\nEND_PROGRAM\n"),
        });

        var headFiles = new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal)
        {
            ["src/Pump.st"] = B("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\nEND_VAR\n"),
            ["src/Logo.bin"] = [0, 1, 2, 3],
        };
        headFiles.Remove("src/Old.st");
        headFiles["src/New.st"] = B("PROGRAM Old\nEND_PROGRAM\n");
        foreach (var (path, bytes) in extraHeadFiles ?? [])
        {
            headFiles[path] = bytes;
        }

        var head = github.Commit(repo, "fix", headFiles);
        repo.MergeBases[(mainTip, head)] = start;
        repo.Pulls.Add((new PullInfo(12, "Fix interlock", "jo", When, "main", "fix", head, false), "## Why\n\nThe delay was wrong."));

        // GitHub's own list: the rename joined, the binary without a patch.
        repo.PullFiles[12] = new PullFileList(
        [
            new PullFile("src/Logo.bin", "added", null, null),
            new PullFile("src/New.st", "renamed", "src/Old.st", null),
            new PullFile("src/Pump.st", "modified", null, "@@ -3 +3 @@\n-  run : BOOL;\n+  run : BOOL := TRUE;"),
        ], false);
        return new World { GitHub = github, Repo = repo, Start = start, MainTip = mainTip, Head = head };
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static async Task<(string Token, JsonElement Body)> SnapshotAsync(TestHost host, string? cookie = null)
    {
        var response = await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", cookie);
        response.AssertStatus(HttpStatusCode.OK);
        var body = Json(await response.Body());
        return (body.GetProperty("token").GetString()!, body);
    }

    private static Task<HttpResponseMessage> WithSnapshot(TestHost host, string path, string token, string? cookie = null) =>
        host.GetAsync(path, cookie, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token });

    private static string SignIn(TestHost host, string login = "alice", string token = "alice-token")
    {
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = sessions.CreateAsync(42, login, login, "", token, DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6)).GetAwaiter().GetResult();
        return $"{AccessResolver.SessionCookie}={session.Id}";
    }

    [Fact]
    public async Task The_snapshot_fixes_the_base_tip_the_merge_base_and_the_head()
    {
        var w = Build();
        await using var host = await TestHost.StartAsync(w.GitHub);

        var (token, body) = await SnapshotAsync(host);

        Assert.Equal(w.MainTip, body.GetProperty("baseSha").GetString());
        Assert.Equal(w.Start, body.GetProperty("mergeBaseSha").GetString());
        Assert.Equal(w.Head, body.GetProperty("headSha").GetString());
        Assert.Contains("The delay was wrong.", body.GetProperty("html").GetString()!, StringComparison.Ordinal);
        // Sealed: the SHAs are not readable out of the token, and it is not a credential.
        Assert.DoesNotContain(w.Head, token, StringComparison.Ordinal);
    }

    /// <summary>
    /// A push between the reads would pair one head with another head's merge-base. The pull request
    /// is read again after the merge-base; when it moved, resolution starts over.
    /// </summary>
    [Fact]
    public async Task A_pull_request_that_moves_while_it_is_read_is_read_again()
    {
        var w = Build();
        var moved = w.GitHub.Commit(w.Repo, "fix2", w.Repo.Commits[w.Head].ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        w.Repo.Commits[moved]["src/Pump.st"] = B("PROGRAM Pump\nVAR\n  run : BOOL := FALSE;\nEND_VAR\n");
        w.Repo.MergeBases[(w.MainTip, moved)] = w.Start;
        // The second read of the pull request (the check after the merge-base) sees a push.
        w.GitHub.BeforePullRead = n =>
        {
            if (n == 2)
            {
                w.Open(moved);
            }
        };
        await using var host = await TestHost.StartAsync(w.GitHub);

        var (_, body) = await SnapshotAsync(host);

        Assert.Equal(moved, body.GetProperty("headSha").GetString());
    }

    /// <summary>
    /// The base is part of the revision: a merge to main during resolution moves the merge-base and
    /// the hunks even though the head never changed, so it restarts resolution too.
    /// </summary>
    [Fact]
    public async Task A_base_that_moves_while_the_pull_request_is_read_is_read_again()
    {
        var w = Build();
        var newTip = w.GitHub.Commit(w.Repo, "main-later", new Dictionary<string, byte[]>(w.Repo.Commits[w.MainTip], StringComparer.Ordinal) { ["src/Later2.st"] = B("x") });
        w.Repo.MergeBases[(newTip, w.Head)] = w.Start;
        w.GitHub.BeforePullRead = n =>
        {
            if (n == 2)
            {
                w.Repo.Refs["main"] = newTip;
            }
        };
        await using var host = await TestHost.StartAsync(w.GitHub);

        var (_, body) = await SnapshotAsync(host);

        Assert.Equal(newTip, body.GetProperty("baseSha").GetString());
    }

    [Fact]
    public async Task A_pull_request_that_never_stops_moving_gets_no_snapshot()
    {
        var w = Build();
        var other = w.GitHub.Commit(w.Repo, "fix2", new Dictionary<string, byte[]>(w.Repo.Commits[w.Head], StringComparer.Ordinal) { ["src/X.st"] = B("x") });
        w.Repo.MergeBases[(w.MainTip, other)] = w.Start;
        w.GitHub.BeforePullRead = n => w.Open(n % 2 == 0 ? other : w.Head);
        await using var host = await TestHost.StartAsync(w.GitHub);

        (await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot")).AssertStatus(HttpStatusCode.BadGateway);
    }

    /// <summary>
    /// The inventory is Versio's tree diff from the merge-base: main's later file is not in it, the
    /// rename is one row because GitHub said so, and every file says whether a line can take a
    /// comment — the binary cannot, and says why.
    /// </summary>
    [Fact]
    public async Task The_files_are_the_whole_change_from_the_merge_base_with_GitHub_deciding_where_lines_take_comments()
    {
        var w = Build();
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (token, _) = await SnapshotAsync(host);

        var response = await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", token);

        response.AssertStatus(HttpStatusCode.OK);
        var body = Json(await response.Body());
        Assert.Equal("pull", body.GetProperty("patchSource").GetString());
        var files = body.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);
        Assert.Equal(["src/Logo.bin", "src/New.st", "src/Pump.st"], files.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("src/Later.st", files.Keys);
        Assert.Equal("renamed", files["src/New.st"].GetProperty("change").GetString());
        Assert.Equal("src/Old.st", files["src/New.st"].GetProperty("previousPath").GetString());
        Assert.True(files["src/Pump.st"].GetProperty("inline").GetBoolean());
        Assert.False(files["src/Logo.bin"].GetProperty("inline").GetBoolean());
        Assert.Equal("no-patch", files["src/Logo.bin"].GetProperty("inlineUnavailable").GetString());
    }

    /// <summary>
    /// GitHub's pull request file list follows the LIVE head. A push during the listing would hand
    /// this revision another revision's hunks, so the list is refused and the compare of the
    /// snapshot's own two commits supplies them.
    /// </summary>
    [Fact]
    public async Task Patches_come_from_the_snapshots_own_compare_when_the_head_moves_during_the_listing()
    {
        var w = Build();
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (token, _) = await SnapshotAsync(host);
        var reads = 0;
        w.GitHub.BeforePullRead = _ =>
        {
            // The files call reads the pull request before and after listing: the second sees a push.
            if (++reads == 2)
            {
                w.Open(new string('9', 40));
            }
        };
        w.Repo.CompareFiles[(w.Start, w.Head)] = new PullFileList([new PullFile("src/Pump.st", "modified", null, "@@ -3 +3 @@")], false);

        var body = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", token)).Body());

        Assert.Equal("compare", body.GetProperty("patchSource").GetString());
        var files = body.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);
        Assert.True(files["src/Pump.st"].GetProperty("inline").GetBoolean());
        // Not in the compare's list, and that list was not cut: GitHub's diff does not name it.
        Assert.Equal("not-in-github-diff", files["src/Logo.bin"].GetProperty("inlineUnavailable").GetString());
        Assert.Contains(w.GitHub.Reads, r => r.What == $"compare-files {w.Start}...{w.Head}");
    }

    /// <summary>
    /// GitHub's compare stops at 300 files. A 301-file change still lists all 301 — the list is
    /// Versio's — and the one past GitHub's cap takes file-level comments only, saying why.
    /// </summary>
    [Fact]
    public async Task A_301_file_change_lists_every_file_and_the_one_past_GitHubs_cap_takes_file_level_comments_only()
    {
        var extra = Enumerable.Range(0, 298).ToDictionary(i => $"gen/F{i:D3}.st", i => B($"PROGRAM F{i}\nEND_PROGRAM\n"));
        var w = Build(extraHeadFiles: extra);
        w.Repo.PullFiles.Remove(12);
        var all = w.Repo.Commits[w.Head].Keys.Where(k => !w.Repo.Commits[w.Start].ContainsKey(k) || !w.Repo.Commits[w.Start][k].SequenceEqual(w.Repo.Commits[w.Head][k]))
            .Order(StringComparer.Ordinal).ToList();
        w.Repo.CompareFiles[(w.Start, w.Head)] = new PullFileList(all.Take(300).Select(p => new PullFile(p, "added", null, "@@ -0,0 +1 @@")).ToList(), true);
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (token, _) = await SnapshotAsync(host);

        var body = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", token)).Body());

        var files = body.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(302, body.GetProperty("total").GetInt32()); // 301 added or changed, and Old.st deleted
        Assert.Equal(302, files.Count);
        Assert.True(body.GetProperty("githubListCut").GetBoolean());
        Assert.Contains(files, f => f.GetProperty("inlineUnavailable").GetString() == "list-cut");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task A_missing_or_forged_snapshot_is_refused(string? token)
    {
        var w = Build();
        await using var host = await TestHost.StartAsync(w.GitHub);

        var response = token is null
            ? await host.GetAsync("/api/repos/acme/plant/pulls/12/files")
            : await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", token);

        response.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Contains("snapshot-invalid", await response.Body(), StringComparison.Ordinal);
    }

    /// <summary>A snapshot is for one pull request and one viewer: valid elsewhere is not valid here.</summary>
    [Fact]
    public async Task A_snapshot_for_another_pull_request_or_another_viewer_is_refused()
    {
        var w = Build();
        w.Repo.Pulls.Add((new PullInfo(13, "Other", "jo", When, "main", "fix", w.Head, false), ""));
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (anonymous, _) = await SnapshotAsync(host);
        var alice = SignIn(host);
        var (alices, _) = await SnapshotAsync(host, alice);

        (await WithSnapshot(host, "/api/repos/acme/plant/pulls/13/files", anonymous)).AssertStatus(HttpStatusCode.BadRequest);
        (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", alices)).AssertStatus(HttpStatusCode.BadRequest);
        (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", anonymous, alice)).AssertStatus(HttpStatusCode.BadRequest);
        (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", alices, alice)).AssertStatus(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_expired_snapshot_asks_for_a_reload_rather_than_moving_the_review_silently()
    {
        var w = Build();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var host = await TestHost.StartAsync(w.GitHub, services: s => s.AddSingleton<IClock>(clock));
        var (token, _) = await SnapshotAsync(host);

        clock.Advance(Versio.Web.Review.ReviewSnapshots.Lifetime + TimeSpan.FromMinutes(1));
        var response = await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", token);

        response.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("snapshot-expired", await response.Body(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The seal proves integrity, not access. A private repository's snapshot, replayed by someone
    /// who cannot read the repository, is "not found" — access is decided before the token is opened.
    /// </summary>
    [Fact]
    public async Task A_valid_snapshot_grants_nothing_access_is_decided_first()
    {
        var w = Build(isPrivate: true);
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (token, _) = await SnapshotAsync(host, SignIn(host));

        var response = await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files", token);

        response.AssertStatus(HttpStatusCode.NotFound);
        Assert.DoesNotContain("Pump", await response.Body(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The page polls status to notice that its revision is no longer the pull request's: a push
    /// moves the head, a merge to main moves the base — either one is "stale", and says which.
    /// </summary>
    [Fact]
    public async Task Status_says_stale_when_the_head_or_the_base_moves_after_the_review_loaded()
    {
        var w = Build();
        await using var host = await TestHost.StartAsync(w.GitHub);
        var alice = SignIn(host);
        var (token, _) = await SnapshotAsync(host, alice);

        var fresh = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/status", token, alice)).Body());
        w.Repo.Refs["main"] = w.Start;
        var baseMoved = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/status", token, alice)).Body());
        w.Repo.Refs["main"] = w.MainTip;
        w.Open(new string('7', 40));
        var headMoved = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/status", token, alice)).Body());

        Assert.False(fresh.GetProperty("stale").GetBoolean());
        Assert.True(baseMoved.GetProperty("stale").GetBoolean());
        Assert.True(baseMoved.GetProperty("baseMoved").GetBoolean());
        Assert.False(baseMoved.GetProperty("headMoved").GetBoolean());
        Assert.True(headMoved.GetProperty("stale").GetBoolean());
        Assert.True(headMoved.GetProperty("headMoved").GetBoolean());
    }

    /// <summary>
    /// GraphQL needs a token. Signed out, the live summary is unknown and says why; unknown is never
    /// passed off as "unchanged". The checks — REST — still answer, for the snapshot's head.
    /// </summary>
    [Fact]
    public async Task Signed_out_the_summary_is_unknown_and_says_why_while_the_checks_still_answer()
    {
        var w = Build();
        w.Repo.Checks[w.Head] = [new CheckInfo("build", "completed", "success", "https://ci.example/1", "check-run")];
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (token, _) = await SnapshotAsync(host);

        var status = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/status", token)).Body());

        Assert.Equal("sign-in", status.GetProperty("summaryError").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("summary").ValueKind);
        Assert.Equal("success", status.GetProperty("checks")[0].GetProperty("conclusion").GetString());
    }

    /// <summary>
    /// Thread bodies are Markdown by anyone who can comment. They arrive rendered by Core — script
    /// escaped, never a tag — with authors and paths spelled.
    /// </summary>
    [Fact]
    public async Task Threads_arrive_rendered_by_Core_with_their_anchors_and_no_live_markup()
    {
        var w = Build();
        w.Repo.Threads[12] = new ReviewThreadList(
        [
            new ReviewThread("T1", "src/Pump.st", false, false, 3, null, "RIGHT", 3, "LINE",
                [new ReviewComment("C1", "bo‮b", "<script>alert(1)</script> **Why TRUE?**", When, w.Head, w.Head)], false),
            new ReviewThread("T2", "src/Logo.bin", true, false, null, null, "RIGHT", null, "FILE", [new ReviewComment("C2", "jo", "binary?", When, w.Head, w.Head)], false),
        ], false);
        await using var host = await TestHost.StartAsync(w.GitHub);
        var alice = SignIn(host);
        var (token, _) = await SnapshotAsync(host, alice);

        var body = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/threads", token, alice)).Body());

        var threads = body.GetProperty("threads").EnumerateArray().ToList();
        Assert.Equal(3, threads[0].GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", threads[0].GetProperty("side").GetString());
        Assert.Equal("FILE", threads[1].GetProperty("subject").GetString());
        Assert.True(threads[1].GetProperty("resolved").GetBoolean());
        var comment = threads[0].GetProperty("comments")[0];
        Assert.Equal("boU+202Eb", comment.GetProperty("author").GetString());
        var html = comment.GetProperty("html").GetString()!;
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<strong>Why TRUE?</strong>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Versio's tags never reach a reader — its own, or a forged one typed on github.com — and the
    /// controls follow GitHub's own word on what this viewer may do.
    /// </summary>
    [Fact]
    public async Task Threads_and_comments_arrive_without_any_versio_tag_and_with_what_the_viewer_may_do()
    {
        var w = Build();
        // One tag inline (forged on github.com: Core's Markdown would show it as text) and Versio's
        // own on a line of its own (Core drops that one anyway). Neither may reach the page.
        var body = "Fine <!-- versio:anchor v1 {\"kind\":\"net\"} --> by me.\n\n<!-- versio:op v1 1760000000000-abcdefghijklmnop -->";
        w.Repo.Threads[12] = new ReviewThreadList(
        [
            new ReviewThread("T1", "src/Pump.st", false, false, 3, null, "RIGHT", 3, "LINE",
                [new ReviewComment("C1", "alice", body, When, w.Head, w.Head, CanEdit: true, CanDelete: true)], false, CanReply: true, CanResolve: true),
        ], false);
        w.Repo.Timelines[12] = new Timeline([new TimelineItem("comment", "alice", When, "Forged <!-- versio:anchor v1 {} --> tag", null, null, "IC_1", CanEdit: true)], false);
        await using var host = await TestHost.StartAsync(w.GitHub);
        var alice = SignIn(host);
        var (token, _) = await SnapshotAsync(host, alice);

        var thread = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/threads", token, alice)).Body()).GetProperty("threads")[0];
        var item = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/timeline", token, alice)).Body()).GetProperty("items")[0];

        var comment = thread.GetProperty("comments")[0];
        Assert.True(thread.GetProperty("canReply").GetBoolean());
        Assert.True(thread.GetProperty("canResolve").GetBoolean());
        Assert.True(comment.GetProperty("canEdit").GetBoolean());
        Assert.DoesNotContain("versio:", comment.GetProperty("markdown").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("versio:", comment.GetProperty("html").GetString()!, StringComparison.Ordinal);
        Assert.Equal("IC_1", item.GetProperty("id").GetString());
        Assert.DoesNotContain("versio:", item.GetProperty("html").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("versio:", item.GetProperty("markdown").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The branch's commits are live; the ones after the snapshot's head arrived since the review
    /// loaded. They are marked, so nobody mistakes them for reviewed.
    /// </summary>
    [Fact]
    public async Task Commits_after_the_snapshots_head_are_marked_as_not_in_the_review()
    {
        var w = Build();
        var later = new string('8', 40);
        w.Repo.PullCommits[12] = new PullCommitList(
        [
            new PullCommit(w.Head, "Fix the default", "jo", When),
            new PullCommit(later, "And another", "jo", When.AddMinutes(5)),
        ], false);
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (token, _) = await SnapshotAsync(host);

        var body = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/commits", token)).Body());

        var commits = body.GetProperty("commits").EnumerateArray().ToList();
        Assert.True(body.GetProperty("headFound").GetBoolean());
        Assert.True(commits[0].GetProperty("inSnapshot").GetBoolean());
        Assert.False(commits[1].GetProperty("inSnapshot").GetBoolean());
    }

    [Fact]
    public async Task The_timeline_renders_comments_and_reviews_and_spells_commit_headlines()
    {
        var w = Build();
        w.Repo.Timelines[12] = new Timeline(
        [
            new TimelineItem("comment", "jo", When, "Looks **fine**", null, null),
            new TimelineItem("commit", "jo", When, "Fix‮default", null, w.Head),
            new TimelineItem("review", "sam", When, "", "APPROVED", null),
        ], false);
        await using var host = await TestHost.StartAsync(w.GitHub);
        var alice = SignIn(host);
        var (token, _) = await SnapshotAsync(host, alice);

        var items = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/timeline", token, alice)).Body()).GetProperty("items").EnumerateArray().ToList();

        Assert.Contains("<strong>fine</strong>", items[0].GetProperty("html").GetString()!, StringComparison.Ordinal);
        Assert.Equal("FixU+202Edefault", items[1].GetProperty("text").GetString());
        Assert.Equal("APPROVED", items[2].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("html").ValueKind);
    }

    /// <summary>
    /// The drawing comes from the snapshot's commits, not the branch: a push after the review loaded
    /// changes nothing the page combines until it reloads.
    /// </summary>
    [Fact]
    public async Task A_file_is_drawn_from_the_snapshots_commits_even_after_the_branch_moves()
    {
        var w = Build();
        await using var host = await TestHost.StartAsync(w.GitHub);
        var (token, _) = await SnapshotAsync(host);
        var pushed = w.GitHub.Commit(w.Repo, "fix", new Dictionary<string, byte[]>(w.Repo.Commits[w.Head], StringComparer.Ordinal)
        {
            ["src/Pump.st"] = B("PROGRAM Pump\nVAR\n  run : BOOL := PUSHED_LATER;\nEND_VAR\n"),
        });
        w.Open(pushed);

        var raw = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files/view?path=src/Pump.st&view=raw", token)).Body());
        var rendered = Json(await (await WithSnapshot(host, "/api/repos/acme/plant/pulls/12/files/view?path=src/Pump.st", token)).Body());

        Assert.Equal("raw", raw.GetProperty("coverage").GetString());
        Assert.Equal("partial", rendered.GetProperty("coverage").GetString());
        var html = raw.GetProperty("view").GetProperty("html").GetString()!;
        Assert.Contains("TRUE", html, StringComparison.Ordinal);
        Assert.DoesNotContain("PUSHED_LATER", html, StringComparison.Ordinal);
    }
}
