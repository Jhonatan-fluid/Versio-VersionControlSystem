using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Api;
using Versio.Web.Auth;
using Versio.Web.Review;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Reviews (docs/web/PLAN.md, *Pull request review*, C2): bound to the commit they are created on,
/// resumed only when they are the viewer's and on the head under review, an older one submitted only
/// as what it is, and never a second write for one click.
/// </summary>
public sealed class PullReviewWriteTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private const string Patch = "@@ -3 +3 @@\n-  run : BOOL;\n+  run : BOOL := TRUE;";

    private sealed record World(FakeGitHub GitHub, FakeGitHub.FakeRepo Repo, string Head);

    private static World Build()
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var start = github.Commit(repo, "start", new Dictionary<string, byte[]> { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\nVAR\n  run : BOOL;\nEND_VAR\n") });
        github.Commit(repo, "main", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal));
        var head = github.Commit(repo, "fix", new Dictionary<string, byte[]> { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\nEND_VAR\n") });
        repo.MergeBases[(repo.Refs["main"], head)] = start;
        repo.Pulls.Add((new PullInfo(12, "Fix", "jo", When, "main", "fix", head, false), "Why"));
        repo.PullFiles[12] = new PullFileList([new PullFile("src/Pump.st", "modified", null, Patch)], false);
        return new World(github, repo, head);
    }

    private static readonly Dictionary<string, string?> WritesOn = new()
    {
        ["Web:Writes:Enabled"] = "true",
        ["Web:Writes:Repositories:0"] = "acme/plant",
        ["Web:Writes:SingleInstance"] = "true",
    };

    private static async Task<(TestHost Host, FakeWriter Writer, string Alice, string Token)> StartAsync(World w)
    {
        var writer = new FakeWriter { Repo = w.Repo };
        var host = await TestHost.StartAsync(w.GitHub, WritesOn, s => s.AddSingleton<IRepoWriter>(writer));
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6));
        var alice = $"{AccessResolver.SessionCookie}={session.Id}";
        var snapshot = await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice);
        return (host, writer, alice, JsonDocument.Parse(await snapshot.Body()).RootElement.GetProperty("token").GetString()!);
    }

    private static string Key() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> Post(TestHost host, string alice, string token, string path, object body) =>
        host.SendAsync(HttpMethod.Post, "/api/repos/acme/plant/pulls/12" + path, alice,
            new Dictionary<string, string> { [AuthEndpoints.CsrfHeader] = "1", [PullEndpoints.SnapshotHeader] = token, ["Sec-Fetch-Site"] = "same-origin" },
            JsonSerializer.Serialize(body));

    private static async Task<JsonElement> Get(TestHost host, string alice, string token, string path) =>
        JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/pulls/12" + path, alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token })).Body()).RootElement;

    private static PendingReview Seed(World w, string commit, string id = "PRR_seeded")
    {
        var pending = new PendingReview(id, "alice", commit, [new PendingComment("PRRC_1", "src/Pump.st", "Draft")]);
        w.Repo.Pending[(12, "alice-token")] = pending;
        return pending;
    }

    // ---- one-shot reviews -----------------------------------------------------------------------

    [Fact]
    public async Task An_approval_is_one_call_on_the_snapshots_head_in_the_persons_name()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        var response = await Post(host, alice, token, "/reviews", new { opKey = Key(), @event = "APPROVE", body = "" });

        response.AssertStatus(HttpStatusCode.OK);
        var call = Assert.Single(writer.Writes);
        Assert.Equal(("review", "alice", $"{w.Head} APPROVE"), (call.Action, call.Access.Who, call.Detail));
    }

    /// <summary>
    /// The race nothing in GitHub's API closes: a push between the server's check and GitHub's submit.
    /// The approval still names the reviewed commit — the snapshot's head — and the live state then
    /// says the head moved, which the page reports (C2).
    /// </summary>
    [Fact]
    public async Task A_push_landing_mid_submit_leaves_the_approval_on_the_reviewed_commit_and_the_move_is_said()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.BeforeEffect = () => w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { HeadSha = new string('9', 40) }, "Why");

        (await Post(host, alice, token, "/reviews", new { opKey = Key(), @event = "APPROVE", body = "" })).AssertStatus(HttpStatusCode.OK);
        var status = await Get(host, alice, token, "/status");

        Assert.Equal(w.Head, writer.Reviews.Single().Commit);
        Assert.True(status.GetProperty("headMoved").GetBoolean());
    }

    [Fact]
    public async Task A_request_for_changes_must_say_what_and_an_unknown_event_is_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/reviews", new { opKey = Key(), @event = "REQUEST_CHANGES", body = " " })).AssertStatus(HttpStatusCode.BadRequest);
        (await Post(host, alice, token, "/reviews", new { opKey = Key(), @event = "MERGE", body = "x" })).AssertStatus(HttpStatusCode.BadRequest);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task A_lost_answer_to_an_approval_is_found_by_its_tag_not_approved_twice()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);

        var response = await Post(host, alice, token, "/reviews", new { opKey = Key(), @event = "APPROVE", body = "" });

        response.AssertStatus(HttpStatusCode.OK);
        Assert.True(JsonDocument.Parse(await response.Body()).RootElement.GetProperty("reconciled").GetBoolean());
        Assert.Single(writer.Reviews);
    }

    [Fact]
    public async Task A_one_shot_review_beside_a_pending_one_is_refused_with_why()
    {
        var w = Build();
        Seed(w, w.Head);
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        var response = await Post(host, alice, token, "/reviews", new { opKey = Key(), @event = "APPROVE", body = "" });

        response.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("pending-exists", await response.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    // ---- the pending review ---------------------------------------------------------------------

    /// <summary>The first draft comment creates the pending review ON the snapshot's head; the next joins it.</summary>
    [Fact]
    public async Task Draft_comments_create_one_pending_review_on_the_head_and_join_it()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/reviews/pending/threads", new { opKey = Key(), body = "First", path = "src/Pump.st", line = 3, side = "RIGHT" })).AssertStatus(HttpStatusCode.OK);
        (await Post(host, alice, token, "/reviews/pending/threads", new { opKey = Key(), body = "Second", path = "src/Pump.st" })).AssertStatus(HttpStatusCode.OK);
        var pending = (await Get(host, alice, token, "/reviews/pending")).GetProperty("review");

        Assert.Equal(["start-pending", "pending-thread", "pending-thread"], writer.Writes.Select(c => c.Action));
        Assert.Equal(w.Head, writer.Writes[0].Detail);
        Assert.True(pending.GetProperty("onSnapshotHead").GetBoolean());
        Assert.Equal(2, pending.GetProperty("comments").GetArrayLength());
    }

    [Fact]
    public async Task A_pending_review_on_the_head_is_submitted_as_approval()
    {
        var w = Build();
        var pending = Seed(w, w.Head);
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.ReviewNodes[pending.Id] = (12, "alice-token", "alice", "PENDING", w.Head);

        var response = await Post(host, alice, token, $"/reviews/{pending.Id}/submit", new { opKey = Key(), @event = "APPROVE", body = "" });

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal((pending.Id, w.Head, "APPROVE"), writer.Reviews.Select(r => (r.Id, r.Commit, r.Event)).Single());
    }

    /// <summary>
    /// A draft review on an older commit is never quietly submitted as if it were about this head:
    /// drafting into it and submitting it are refused, and the historical route submits it only for
    /// the commit it was created on, named — any other commit named is refused.
    /// </summary>
    [Fact]
    public async Task A_pending_review_on_an_older_commit_is_submitted_only_as_historical_naming_its_commit()
    {
        var w = Build();
        var older = new string('4', 40);
        var pending = Seed(w, older);
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.ReviewNodes[pending.Id] = (12, "alice-token", "alice", "PENDING", older);

        var draft = await Post(host, alice, token, "/reviews/pending/threads", new { opKey = Key(), body = "More", path = "src/Pump.st" });
        var submit = await Post(host, alice, token, $"/reviews/{pending.Id}/submit", new { opKey = Key(), @event = "APPROVE", body = "" });
        var wrong = await Post(host, alice, token, $"/reviews/{pending.Id}/submit-historical", new { opKey = Key(), @event = "APPROVE", body = "", commit = w.Head });
        var right = await Post(host, alice, token, $"/reviews/{pending.Id}/submit-historical", new { opKey = Key(), @event = "APPROVE", body = "", commit = older });

        draft.AssertStatus(HttpStatusCode.Conflict);
        submit.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("pending-older", await submit.Body(), StringComparison.Ordinal);
        wrong.AssertStatus(HttpStatusCode.Conflict);
        right.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(older, writer.Reviews.Single().Commit);
    }

    /// <summary>A review id from another pull request, or another person's review, is never theirs to submit or discard.</summary>
    [Fact]
    public async Task Another_pull_requests_review_or_another_persons_is_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.ReviewNodes["PRR_elsewhere"] = (99, "alice-token", "alice", "PENDING", w.Head);
        writer.ReviewNodes["PRR_bobs"] = (12, "bob-token", "bob", "PENDING", w.Head);

        (await Post(host, alice, token, "/reviews/PRR_elsewhere/submit", new { opKey = Key(), @event = "APPROVE", body = "" })).AssertStatus(HttpStatusCode.NotFound);
        (await Post(host, alice, token, "/reviews/PRR_bobs/submit", new { opKey = Key(), @event = "APPROVE", body = "" })).AssertStatus(HttpStatusCode.Conflict);
        (await Post(host, alice, token, "/reviews/PRR_bobs/discard", new { opKey = Key() })).AssertStatus(HttpStatusCode.Conflict);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task A_pending_review_is_discarded_and_gone()
    {
        var w = Build();
        var pending = Seed(w, new string('4', 40));
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.ReviewNodes[pending.Id] = (12, "alice-token", "alice", "PENDING", pending.Commit);

        (await Post(host, alice, token, $"/reviews/{pending.Id}/discard", new { opKey = Key() })).AssertStatus(HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, (await Get(host, alice, token, "/reviews/pending")).GetProperty("review").ValueKind);
    }

    // ---- reviewers and viewed files ---------------------------------------------------------------

    [Fact]
    public async Task Reviewers_are_requested_by_login_and_anything_else_is_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/reviewers", new { opKey = Key(), add = new[] { "bad login!" } })).AssertStatus(HttpStatusCode.BadRequest);
        (await Post(host, alice, token, "/reviewers", new { opKey = Key(), add = new[] { "sam" }, remove = new[] { "lee" } })).AssertStatus(HttpStatusCode.OK);

        Assert.Equal(("sam", "lee"), writer.ReviewerChanges.Single());
    }

    [Fact]
    public async Task A_file_is_marked_viewed_on_this_pull_request_and_the_marks_read_back()
    {
        var w = Build();
        w.Repo.Viewed[(12, "alice-token")] = ["src/Other.st"];
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/viewed", new { opKey = Key(), path = "src/Pump.st", viewed = true })).AssertStatus(HttpStatusCode.OK);
        var viewed = (await Get(host, alice, token, "/viewed")).GetProperty("paths").EnumerateArray().Select(p => p.GetString()).ToList();

        Assert.Equal(("src/Pump.st", true), writer.ViewedMarks.Single());
        Assert.Equal(["src/Other.st"], viewed);
    }

    /// <summary>"Changes since your last review" needs the commit the viewer last reviewed; GitHub refuses an author's approval, so the page needs that too.</summary>
    [Fact]
    public async Task Status_names_the_viewers_last_reviewed_commit_and_whether_they_wrote_the_pull_request()
    {
        var w = Build();
        w.Repo.LastReviewCommit[(12, "alice-token")] = new string('4', 40);
        w.Repo.PullAuthorToken[12] = "alice-token";
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var summary = (await Get(host, alice, token, "/status")).GetProperty("summary");

        Assert.Equal(new string('4', 40), summary.GetProperty("viewerLastReviewCommit").GetString());
        Assert.True(summary.GetProperty("viewerIsAuthor").GetBoolean());
    }
}
