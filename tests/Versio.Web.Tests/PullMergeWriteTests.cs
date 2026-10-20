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
/// Merging (docs/web/PLAN.md, *Pull request review*, slice 6, S11): only what was reviewed, only
/// when anyone could merge it — an administrator included — and only with the merge tier switched on.
/// </summary>
public sealed class PullMergeWriteTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

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
        return new World(github, repo, head);
    }

    private static Dictionary<string, string?> Settings(bool merge) => new()
    {
        ["Web:Writes:Enabled"] = "true",
        ["Web:Writes:Repositories:0"] = "acme/plant",
        ["Web:Writes:SingleInstance"] = "true",
        ["Web:Writes:Merge"] = merge ? "true" : "false",
    };

    private static async Task<(TestHost Host, FakeWriter Writer, string Alice, string Token, JsonElement Snapshot)> StartAsync(World w, bool merge = true)
    {
        var writer = new FakeWriter { Repo = w.Repo };
        var host = await TestHost.StartAsync(w.GitHub, Settings(merge), s => s.AddSingleton<IRepoWriter>(writer));
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6));
        var alice = $"{AccessResolver.SessionCookie}={session.Id}";
        var snapshot = JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice)).Body()).RootElement;
        return (host, writer, alice, snapshot.GetProperty("token").GetString()!, snapshot);
    }

    private static string Key() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> Post(TestHost host, string alice, string token, string path, object body) =>
        host.SendAsync(HttpMethod.Post, "/api/repos/acme/plant/pulls/12" + path, alice,
            new Dictionary<string, string> { [AuthEndpoints.CsrfHeader] = "1", [PullEndpoints.SnapshotHeader] = token, ["Sec-Fetch-Site"] = "same-origin" },
            JsonSerializer.Serialize(body));

    private static async Task<JsonElement> Get(TestHost host, string alice, string token, string path) =>
        JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/pulls/12" + path, alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token })).Body()).RootElement;

    private static void PushTo(World w, string sha) => w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { HeadSha = sha }, w.Repo.Pulls[0].Body);

    [Fact]
    public async Task A_merge_names_the_reviewed_head_as_GitHubs_precondition_in_the_persons_name()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        var response = await Post(host, alice, token, "/merge", new { opKey = Key(), method = "SQUASH", title = "Fix the pump (#12)" });

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal((12, w.Head, "SQUASH"), writer.Merges.Single());
        Assert.Equal("alice", writer.Calls.Single(c => c.Action == "merge").Access.Who);
        Assert.Equal("MERGED", w.Repo.PullStates[12]);
    }

    /// <summary>S11: with writes on but the merge tier off, merging does not exist, and the page is told not to offer it.</summary>
    [Fact]
    public async Task Without_the_merge_tier_no_merge_route_exists_and_none_is_offered()
    {
        var w = Build();
        var (host, writer, alice, token, snapshot) = await StartAsync(w, merge: false);
        await using var __ = host;

        (await Post(host, alice, token, "/merge", new { opKey = Key(), method = "MERGE" })).AssertStatus(HttpStatusCode.NotFound);
        (await Post(host, alice, token, "/update-branch", new { opKey = Key() })).AssertStatus(HttpStatusCode.NotFound);
        (await Post(host, alice, token, "/auto-merge", new { opKey = Key(), enable = true, method = "MERGE" })).AssertStatus(HttpStatusCode.NotFound);
        Assert.False(snapshot.GetProperty("canMerge").GetBoolean());
        Assert.False((await Get(host, alice, token, "/merge-state")).GetProperty("tier").GetBoolean());
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task With_the_tier_on_the_snapshot_says_merging_can_be_offered()
    {
        var w = Build();
        var (host, _, alice, token, snapshot) = await StartAsync(w);
        await using var __ = host;

        Assert.True(snapshot.GetProperty("canMerge").GetBoolean());
        var state = await Get(host, alice, token, "/merge-state");
        Assert.True(state.GetProperty("mergeable").GetBoolean());
        Assert.Equal(["MERGE", "SQUASH", "REBASE"], state.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task A_head_or_a_base_that_moved_since_the_review_was_opened_merges_nothing()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        PushTo(w, new string('9', 40));
        var pushed = await Post(host, alice, token, "/merge", new { opKey = Key(), method = "MERGE" });
        PushTo(w, w.Head);
        w.Repo.Refs["main"] = new string('8', 40);
        var moved = await Post(host, alice, token, "/merge", new { opKey = Key(), method = "MERGE" });

        pushed.AssertStatus(HttpStatusCode.Conflict);
        moved.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("snapshot-stale", await moved.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Merges);
    }

    /// <summary>
    /// The race the server's check cannot close: a push after it, before GitHub merges. GitHub's sha
    /// precondition closes it — nothing is merged, and GitHub's own words say why.
    /// </summary>
    [Fact]
    public async Task A_push_landing_after_the_checks_is_refused_by_GitHubs_precondition_and_nothing_is_merged()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;
        writer.BeforeEffect = () => PushTo(w, new string('9', 40));

        var response = await Post(host, alice, token, "/merge", new { opKey = Key(), method = "MERGE" });

        response.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("Head branch was modified", await response.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Merges);
        Assert.False(w.Repo.PullStates.ContainsKey(12));
    }

    /// <summary>
    /// No admin bypass, ever: GitHub would let an administrator merge past required reviews and
    /// checks, so Versio merges only in a state anyone could merge in, and refuses the rest for everyone.
    /// </summary>
    [Theory]
    [InlineData("BLOCKED")]
    [InlineData("BEHIND")]
    [InlineData("DIRTY")]
    [InlineData("DRAFT")]
    [InlineData("UNKNOWN")]
    public async Task Nothing_is_merged_past_what_protects_the_branch_for_anyone(string status)
    {
        var w = Build();
        w.Repo.MergeStatus[12] = status;
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        var response = await Post(host, alice, token, "/merge", new { opKey = Key(), method = "MERGE" });
        var state = await Get(host, alice, token, "/merge-state");

        response.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("not-mergeable", await response.Body(), StringComparison.Ordinal);
        Assert.False(state.GetProperty("mergeable").GetBoolean());
        Assert.DoesNotContain(writer.Calls, c => c.Action == "merge");
    }

    [Theory]
    [InlineData("UNSTABLE")]
    [InlineData("HAS_HOOKS")]
    public async Task A_state_anyone_could_merge_in_is_merged(string status)
    {
        var w = Build();
        w.Repo.MergeStatus[12] = status;
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/merge", new { opKey = Key(), method = "MERGE" })).AssertStatus(HttpStatusCode.OK);
        Assert.Single(writer.Merges);
    }

    [Fact]
    public async Task A_method_the_repository_does_not_allow_is_refused()
    {
        var w = Build();
        w.Repo.MergeMethods.Remove("REBASE");
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/merge", new { opKey = Key(), method = "REBASE" })).AssertStatus(HttpStatusCode.UnprocessableEntity);
        (await Post(host, alice, token, "/merge", new { opKey = Key(), method = "FAST_FORWARD" })).AssertStatus(HttpStatusCode.BadRequest);
        Assert.Empty(writer.Merges);
    }

    /// <summary>Failing closed: when GitHub cannot say whether a merge is allowed, none is made or offered.</summary>
    [Fact]
    public async Task When_the_merge_state_cannot_be_read_nothing_is_merged_or_offered()
    {
        var w = Build();
        w.Repo.MergeStateFailure = RepoFailure.Upstream;
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        var response = await Post(host, alice, token, "/merge", new { opKey = Key(), method = "MERGE" });
        var state = await Get(host, alice, token, "/merge-state");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.False(state.TryGetProperty("mergeable", out _));
        Assert.True(state.TryGetProperty("error", out _));
        Assert.Empty(writer.Merges);
    }

    [Fact]
    public async Task A_lost_answer_to_a_merge_is_settled_by_reading_whether_the_reviewed_head_merged()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);
        var request = new { opKey = Key(), method = "MERGE" };

        var first = await Post(host, alice, token, "/merge", request);
        var again = await Post(host, alice, token, "/merge", request);

        first.AssertStatus(HttpStatusCode.OK);
        Assert.True(JsonDocument.Parse(await first.Body()).RootElement.GetProperty("reconciled").GetBoolean());
        again.AssertStatus(HttpStatusCode.OK);
        Assert.Single(writer.Calls, c => c.Action == "merge");
    }

    [Fact]
    public async Task Bringing_the_branch_up_to_date_names_the_reviewed_head_and_then_the_review_is_stale()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/update-branch", new { opKey = Key() })).AssertStatus(HttpStatusCode.OK);
        var status = await Get(host, alice, token, "/status");

        Assert.Equal(w.Head, writer.Calls.Single(c => c.Action == "update-branch").Detail);
        Assert.True(status.GetProperty("headMoved").GetBoolean());
    }

    /// <summary>
    /// The update names a base; retargeted after the page opened, the confirmation is of a base the
    /// pull request no longer has, and nothing is brought in (found by review, 2026-10-20).
    /// </summary>
    [Fact]
    public async Task Bringing_the_branch_up_to_date_is_refused_once_the_base_was_retargeted()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;
        w.GitHub.Commit(w.Repo, "release", new Dictionary<string, byte[]>(w.Repo.Commits[w.Repo.Refs["main"]], StringComparer.Ordinal));
        w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { BaseRef = "release" }, w.Repo.Pulls[0].Body);

        var response = await Post(host, alice, token, "/update-branch", new { opKey = Key() });

        response.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("snapshot-stale", await response.Body(), StringComparison.Ordinal);
        Assert.DoesNotContain(writer.Calls, c => c.Action == "update-branch");
    }

    /// <summary>
    /// Auto-merge is turned on only while the head is the reviewed commit (expectedHeadOid). That is
    /// not a pin — later pushes by people with write access are merged too — so it takes the person's
    /// explicit yes to exactly that.
    /// </summary>
    [Fact]
    public async Task Auto_merge_is_turned_on_only_while_the_head_is_the_reviewed_one_and_off_again()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/auto-merge", new { opKey = Key(), enable = true, method = "SQUASH", acceptsLaterCommits = true })).AssertStatus(HttpStatusCode.OK);
        Assert.Equal(("SQUASH", "alice", w.Head), w.Repo.AutoMerge[12]);
        (await Post(host, alice, token, "/auto-merge", new { opKey = Key(), enable = false })).AssertStatus(HttpStatusCode.OK);

        Assert.False(w.Repo.AutoMerge.ContainsKey(12));
        Assert.Equal(["auto-merge-on", "auto-merge-off"], writer.Writes.Select(c => c.Action));
    }

    [Fact]
    public async Task Auto_merge_is_refused_where_the_repository_does_not_allow_it_or_the_review_is_stale()
    {
        var w = Build();
        w.Repo.AutoMergeAllowed = false;
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/auto-merge", new { opKey = Key(), enable = true, method = "MERGE", acceptsLaterCommits = true })).AssertStatus(HttpStatusCode.UnprocessableEntity);
        w.Repo.AutoMergeAllowed = true;
        PushTo(w, new string('9', 40));
        (await Post(host, alice, token, "/auto-merge", new { opKey = Key(), enable = true, method = "MERGE", acceptsLaterCommits = true })).AssertStatus(HttpStatusCode.Conflict);
        Assert.Empty(writer.Writes);
    }

    /// <summary>Without the person's yes to later commits being merged, auto-merge is not turned on.</summary>
    [Fact]
    public async Task Auto_merge_needs_the_persons_yes_to_later_commits_being_merged()
    {
        var w = Build();
        var (host, writer, alice, token, _) = await StartAsync(w);
        await using var __ = host;

        var response = await Post(host, alice, token, "/auto-merge", new { opKey = Key(), enable = true, method = "MERGE" });

        response.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Contains("consent", await response.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Calls);
    }
}
