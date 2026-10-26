using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Api;
using Versio.Web.Auth;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The conversation's other writes (docs/web/FINISH.md, Step 5): reactions, hiding a comment,
/// locking, the milestone, notifications, dismissing and editing a review, team reviewers — each
/// in the person's name, each on something in THIS pull request, each settled by reading back.
/// </summary>
public sealed class PullConversationWriteTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private sealed record World(FakeGitHub GitHub, FakeGitHub.FakeRepo Repo, string Head);

    private static World Build()
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var files = new Dictionary<string, byte[]> { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\nEND_PROGRAM\n") };
        var start = github.Commit(repo, "start", files);
        github.Commit(repo, "main", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal));
        var head = github.Commit(repo, "fix", new Dictionary<string, byte[]>(files) { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\n(* fixed *)\nEND_PROGRAM\n") });
        repo.MergeBases[(repo.Refs["main"], head)] = start;
        repo.Pulls.Add((new PullInfo(12, "Fix", "jo", When, "main", "fix", head, false), "Why"));
        repo.PullFiles[12] = new PullFileList([], false);
        repo.Milestones.Add(new MilestoneInfo(5, "v2", "open"));
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
        // A conversation comment of this pull request, one of another repository, and reviews: an approval of this one, a comment review, and alice's own.
        writer.Comments["IC_here"] = new FakeWriter.Comment("IC_here", "IssueComment", "acme/plant", 12, "sam", "Looks fine", When, null);
        writer.Comments["IC_elsewhere"] = new FakeWriter.Comment("IC_elsewhere", "IssueComment", "acme/other", 12, "sam", "Elsewhere", When, null);
        writer.ReviewNodes["PRR_approved"] = (12, "sam-token", "sam", "APPROVED", w.Head);
        writer.ReviewNodes["PRR_commented"] = (12, "sam-token", "sam", "COMMENTED", w.Head);
        writer.ReviewNodes["PRR_mine"] = (12, "alice-token", "alice", "COMMENTED", w.Head);
        writer.ReviewNodes["PRR_pending"] = (12, "alice-token", "alice", "PENDING", w.Head);
        var host = await TestHost.StartAsync(w.GitHub, WritesOn, s => s.AddSingleton<IRepoWriter>(writer));
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6));
        var alice = $"{AccessResolver.SessionCookie}={session.Id}";
        var snapshot = await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice);
        return (host, writer, alice, JsonDocument.Parse(await snapshot.Body()).RootElement.GetProperty("token").GetString()!);
    }

    private static string Key() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> Post(TestHost host, string alice, string token, string path, object body, string method = "POST") =>
        host.SendAsync(new HttpMethod(method), "/api/repos/acme/plant/pulls/12" + path, alice,
            new Dictionary<string, string> { [AuthEndpoints.CsrfHeader] = "1", [PullEndpoints.SnapshotHeader] = token, ["Sec-Fetch-Site"] = "same-origin" },
            JsonSerializer.Serialize(body));

    private static async Task<JsonElement> Get(TestHost host, string alice, string token, string path) =>
        JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/pulls/12" + path, alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token })).Body()).RootElement;

    // ---- reactions --------------------------------------------------------------------------------

    [Fact]
    public async Task A_reaction_goes_on_a_comment_a_review_or_the_pull_request_of_this_review_and_nowhere_else()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/reactions", new { opKey = Key(), subject = "IC_here", content = "HEART", on = true })).AssertStatus(HttpStatusCode.OK);
        (await Post(host, alice, token, "/reactions", new { opKey = Key(), subject = "PRR_approved", content = "ROCKET", on = true })).AssertStatus(HttpStatusCode.OK);
        (await Post(host, alice, token, "/reactions", new { opKey = Key(), subject = "PR_12", content = "EYES", on = true })).AssertStatus(HttpStatusCode.OK);
        (await Post(host, alice, token, "/reactions", new { opKey = Key(), subject = "IC_elsewhere", content = "HEART", on = true })).AssertStatus(HttpStatusCode.NotFound);
        (await Post(host, alice, token, "/reactions", new { opKey = Key(), subject = "IC_here", content = "SMILE", on = true })).AssertStatus(HttpStatusCode.BadRequest);
        (await Post(host, alice, token, "/reactions", new { opKey = Key(), subject = "IC_here", content = "HEART", on = false })).AssertStatus(HttpStatusCode.OK);

        Assert.Equal(["react", "react", "react", "unreact"], writer.Writes.Select(c => c.Action));
        Assert.All(writer.Writes, c => Assert.Equal("alice", c.Access.Who));
        Assert.False(writer.Reactions.ContainsKey(("IC_here", "alice", "HEART")));
        Assert.True(writer.Reactions.ContainsKey(("PR_12", "alice", "EYES")));
    }

    /// <summary>A lost answer is settled by reading whether the viewer's reaction is there; it is never sent twice when it is.</summary>
    [Fact]
    public async Task A_lost_answer_to_a_reaction_is_settled_by_reading_it_back()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);

        var response = await Post(host, alice, token, "/reactions", new { opKey = Key(), subject = "IC_here", content = "HEART", on = true });

        response.AssertStatus(HttpStatusCode.OK);
        Assert.True(JsonDocument.Parse(await response.Body()).RootElement.GetProperty("reconciled").GetBoolean());
        Assert.Single(writer.Writes);
    }

    // ---- hiding, locking, milestone, notifications ------------------------------------------------

    [Fact]
    public async Task A_comment_is_hidden_with_a_reason_and_shown_again_and_an_odd_reason_is_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/comments/IC_here/minimize", new { opKey = Key(), classifier = "OUTDATED" })).AssertStatus(HttpStatusCode.OK);
        Assert.Equal("OUTDATED", writer.Minimized["IC_here"]);
        (await Post(host, alice, token, "/comments/IC_here/minimize", new { opKey = Key(), classifier = "RUDE" })).AssertStatus(HttpStatusCode.BadRequest);
        (await Post(host, alice, token, "/comments/IC_elsewhere/minimize", new { opKey = Key(), classifier = "SPAM" })).AssertStatus(HttpStatusCode.NotFound);
        (await Post(host, alice, token, "/comments/IC_here/minimize", new { opKey = Key(), classifier = (string?)null })).AssertStatus(HttpStatusCode.OK);

        Assert.Empty(writer.Minimized);
        Assert.Equal(["minimize", "unminimize"], writer.Writes.Select(c => c.Action));
    }

    [Fact]
    public async Task The_conversation_is_locked_with_a_reason_and_the_status_says_so_then_unlocked()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/lock", new { opKey = Key(), @lock = true, reason = "RESOLVED" })).AssertStatus(HttpStatusCode.OK);
        var locked = (await Get(host, alice, token, "/status")).GetProperty("summary");
        (await Post(host, alice, token, "/lock", new { opKey = Key(), @lock = true, reason = "BORED" })).AssertStatus(HttpStatusCode.BadRequest);
        (await Post(host, alice, token, "/lock", new { opKey = Key(), @lock = false })).AssertStatus(HttpStatusCode.OK);

        Assert.Equal((true, "RESOLVED"), (locked.GetProperty("locked").GetBoolean(), locked.GetProperty("lockReason").GetString()));
        Assert.False(w.Repo.Locked.ContainsKey(12));
        Assert.Equal(["lock", "unlock"], writer.Writes.Select(c => c.Action));
    }

    /// <summary>A milestone is put on, never made: one the repository does not have is refused before GitHub hears of it.</summary>
    [Fact]
    public async Task A_milestone_the_repository_has_is_set_and_read_back_one_it_lacks_is_refused_and_null_clears_it()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/milestone", new { opKey = Key(), number = 9 })).AssertStatus(HttpStatusCode.UnprocessableEntity);
        (await Post(host, alice, token, "/milestone", new { opKey = Key(), number = 5 })).AssertStatus(HttpStatusCode.OK);
        var set = (await Get(host, alice, token, "/status")).GetProperty("summary");
        (await Post(host, alice, token, "/milestone", new { opKey = Key(), number = (int?)null })).AssertStatus(HttpStatusCode.OK);
        var cleared = (await Get(host, alice, token, "/status")).GetProperty("summary");
        var list = JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/milestones", alice)).Body()).RootElement.GetProperty("milestones");

        Assert.Equal(("v2", 5), (set.GetProperty("milestone").GetString(), set.GetProperty("milestoneNumber").GetInt32()));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("milestone").ValueKind);
        Assert.Equal(["milestone", "milestone"], writer.Writes.Select(c => c.Action));
        Assert.Equal("v2", list[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_viewers_notifications_are_set_and_read_back_in_their_own_name()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        Assert.Equal("SUBSCRIBED", (await Get(host, alice, token, "/status")).GetProperty("summary").GetProperty("viewerSubscription").GetString());
        (await Post(host, alice, token, "/subscription", new { opKey = Key(), state = "IGNORED" })).AssertStatus(HttpStatusCode.OK);
        (await Post(host, alice, token, "/subscription", new { opKey = Key(), state = "MUTED" })).AssertStatus(HttpStatusCode.BadRequest);

        Assert.Equal("IGNORED", (await Get(host, alice, token, "/status")).GetProperty("summary").GetProperty("viewerSubscription").GetString());
        Assert.Equal(("subscription", "alice"), (writer.Writes.Single().Action, writer.Writes.Single().Access.Who));
    }

    // ---- reviews ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_approval_is_dismissed_with_a_reason_a_comment_review_cannot_be_and_the_reason_is_required()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/reviews/PRR_approved/dismiss", new { opKey = Key(), body = " " })).AssertStatus(HttpStatusCode.BadRequest);
        (await Post(host, alice, token, "/reviews/PRR_commented/dismiss", new { opKey = Key(), body = "Stale" })).AssertStatus(HttpStatusCode.Conflict);
        (await Post(host, alice, token, "/reviews/PRR_approved/dismiss", new { opKey = Key(), body = "Stale after the rebase" })).AssertStatus(HttpStatusCode.OK);

        Assert.Equal("DISMISSED", writer.ReviewNodes["PRR_approved"].State);
        Assert.Equal(("dismiss", "PRR_approved Stale after the rebase"), (writer.Writes.Single().Action, writer.Writes.Single().Detail));
    }

    /// <summary>Only the viewer's own submitted review: not someone else's, and not a pending one, whose submission writes its words.</summary>
    [Fact]
    public async Task A_review_summary_is_edited_by_its_author_only_and_never_a_pending_one()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/reviews/PRR_approved", new { opKey = Key(), body = "Mine now" }, "PATCH")).AssertStatus(HttpStatusCode.Conflict);
        (await Post(host, alice, token, "/reviews/PRR_pending", new { opKey = Key(), body = "Too soon" }, "PATCH")).AssertStatus(HttpStatusCode.Conflict);
        (await Post(host, alice, token, "/reviews/PRR_mine", new { opKey = Key(), body = "Better words" }, "PATCH")).AssertStatus(HttpStatusCode.OK);

        Assert.StartsWith("Better words\n\n<!-- versio:op v1 ", writer.ReviewBodies["PRR_mine"], StringComparison.Ordinal);
        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task A_lost_answer_to_a_review_edit_is_found_by_its_tag()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);

        var response = await Post(host, alice, token, "/reviews/PRR_mine", new { opKey = Key(), body = "Better words" }, "PATCH");

        response.AssertStatus(HttpStatusCode.OK);
        Assert.True(JsonDocument.Parse(await response.Body()).RootElement.GetProperty("reconciled").GetBoolean());
        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task Teams_are_asked_by_slug_beside_people_and_the_status_names_a_teams_slug()
    {
        var w = Build();
        w.Repo.PullPeople[12] = ([], [new PullReviewer("Controls", "REQUESTED", true) { Slug = "plc-controls" }]);
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Post(host, alice, token, "/reviewers", new { opKey = Key(), add = new[] { "sam" }, addTeams = new[] { "safety" }, removeTeams = new[] { "plc-controls" } })).AssertStatus(HttpStatusCode.OK);
        (await Post(host, alice, token, "/reviewers", new { opKey = Key(), addTeams = new[] { "not a slug" } })).AssertStatus(HttpStatusCode.BadRequest);
        var reviewer = (await Get(host, alice, token, "/status")).GetProperty("summary").GetProperty("reviewers")[0];

        Assert.Equal(("sam,team:safety", "team:plc-controls"), writer.ReviewerChanges.Single());
        Assert.Equal(("Controls", "plc-controls", true), (reviewer.GetProperty("login").GetString(), reviewer.GetProperty("slug").GetString(), reviewer.GetProperty("isTeam").GetBoolean()));
    }

    /// <summary>The review panel edits each draft in place: it needs the draft's words and where it sits, not only its file.</summary>
    [Fact]
    public async Task A_pending_reviews_drafts_carry_their_words_and_lines()
    {
        var w = Build();
        w.Repo.Pending[(12, "alice-token")] = new PendingReview("PRR_pending", "alice", w.Head, [new PendingComment("PRRC_1", "src/Pump.st", "Draft words\n\n<!-- versio:op v1 1-x -->") { Line = 2, StartLine = 1 }]);
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var draft = (await Get(host, alice, token, "/reviews/pending")).GetProperty("review").GetProperty("comments")[0];

        Assert.Equal(("Draft words", 2, 1), (draft.GetProperty("markdown").GetString(), draft.GetProperty("line").GetInt32(), draft.GetProperty("startLine").GetInt32()));
    }
}
