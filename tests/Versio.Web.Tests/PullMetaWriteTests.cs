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
/// Opening pull requests and editing what they say (docs/web/PLAN.md, *Pull request review*, slice 5):
/// in the person's own name, never opened twice for one click, and never overwriting what someone
/// else wrote in the meantime.
/// </summary>
public sealed class PullMetaWriteTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private sealed record World(FakeGitHub GitHub, FakeGitHub.FakeRepo Repo, string Head);

    private static World Build(string? template = null)
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var files = new Dictionary<string, byte[]> { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\nEND_PROGRAM\n") };
        if (template is not null)
        {
            files[".github/PULL_REQUEST_TEMPLATE.md"] = Encoding.UTF8.GetBytes(template);
        }

        var start = github.Commit(repo, "start", files);
        github.Commit(repo, "main", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal));
        github.Commit(repo, "release", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal));
        var head = github.Commit(repo, "fix", new Dictionary<string, byte[]>(files) { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\n(* fixed *)\nEND_PROGRAM\n") });
        github.Commit(repo, "feature", new Dictionary<string, byte[]>(files) { ["src/Valve.st"] = Encoding.UTF8.GetBytes("PROGRAM Valve\nEND_PROGRAM\n") });
        repo.MergeBases[(repo.Refs["main"], head)] = start;
        repo.Pulls.Add((new PullInfo(12, "Fix", "jo", When, "main", "fix", head, false), "Why\n\n<!-- versio:op v1 1-old -->"));
        repo.PullFiles[12] = new PullFileList([], false);
        repo.Labels.AddRange([new PullLabel("safety", "d73a4a"), new PullLabel("plc", "0e8a16")]);
        repo.PullPeople[12] = ([new PullLabel("plc", "0e8a16")], []);
        repo.Assignable.UnionWith(["alice", "sam"]);
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

    private static Dictionary<string, string> Headers(string? token = null)
    {
        var headers = new Dictionary<string, string> { [AuthEndpoints.CsrfHeader] = "1", ["Sec-Fetch-Site"] = "same-origin" };
        if (token is not null)
        {
            headers[PullEndpoints.SnapshotHeader] = token;
        }

        return headers;
    }

    private static Task<HttpResponseMessage> Create(TestHost host, string? alice, object body) =>
        host.SendAsync(HttpMethod.Post, "/api/repos/acme/plant/pulls", alice, Headers(), JsonSerializer.Serialize(body));

    private static Task<HttpResponseMessage> Post(TestHost host, string alice, string token, string path, object body) =>
        host.SendAsync(HttpMethod.Post, "/api/repos/acme/plant/pulls/12" + path, alice, Headers(token), JsonSerializer.Serialize(body));

    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Body()).RootElement;

    // ---- opening one ----------------------------------------------------------------------------

    [Fact]
    public async Task A_pull_request_is_opened_in_the_persons_name_between_two_branches_and_carries_its_tag()
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;
        var key = Key();

        var response = await Create(host, alice, new { opKey = key, @base = "main", head = "feature", title = " Add the valve ", body = "Adds it.", draft = true });

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("13", (await Json(response)).GetProperty("id").GetString());
        var created = Assert.Single(writer.Created);
        Assert.Equal(("main", "feature", "Add the valve", true), (created.Base, created.Head, created.Title, created.Draft));
        Assert.True(ReviewTags.Carries(created.Body, key));
        Assert.Equal("Adds it.", ReviewTags.Strip(created.Body));
        Assert.Equal(("create-pull", "alice"), (writer.Writes.Single().Action, writer.Writes.Single().Access.Who));
    }

    /// <summary>C5 for creation: GitHub opened it and the answer was lost. The retry finds it by its tag; nothing is opened twice.</summary>
    [Fact]
    public async Task A_lost_answer_to_opening_one_is_found_by_its_tag_and_never_opened_twice()
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);
        var request = new { opKey = Key(), @base = "main", head = "feature", title = "Add the valve", body = "", draft = false };

        var first = await Create(host, alice, request);
        var again = await Create(host, alice, request);

        first.AssertStatus(HttpStatusCode.OK);
        Assert.True((await Json(first)).GetProperty("reconciled").GetBoolean());
        Assert.Equal("13", (await Json(again)).GetProperty("id").GetString());
        Assert.Single(writer.Created);
    }

    [Fact]
    public async Task An_answer_lost_before_GitHub_had_it_stays_unknown_and_opens_nothing()
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostBefore);

        var response = await Create(host, alice, new { opKey = Key(), @base = "main", head = "feature", title = "Add the valve", body = "", draft = false });

        response.AssertStatus(HttpStatusCode.Accepted);
        Assert.Equal("unknown", (await Json(response)).GetProperty("outcome").GetString());
        Assert.DoesNotContain(w.Repo.Pulls, p => p.Info.Number == 13);
    }

    /// <summary>
    /// Opening is never repeated on its own: an unknown outcome the retry cannot find stays unknown,
    /// because GitHub may yet show it. Opening it again is the person's deliberate new submission.
    /// </summary>
    [Fact]
    public async Task A_retry_of_an_unknown_opening_that_finds_nothing_sends_nothing()
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostBefore);
        var request = new { opKey = Key(), @base = "main", head = "feature", title = "Add the valve", body = "", draft = false };

        await Create(host, alice, request);
        var retry = await Create(host, alice, request);

        retry.AssertStatus(HttpStatusCode.Accepted);
        Assert.Single(writer.Writes);
        Assert.Empty(writer.Created);
    }

    [Theory]
    [InlineData("main", "main", "Same")]
    [InlineData("main", "bob:feature", "A fork")]
    [InlineData("main", "feature", "")]
    [InlineData("main", "feature", "   ")]
    [InlineData("main", "-feature", "Option-shaped")]
    public async Task Two_different_branches_of_this_repository_and_a_title_are_required(string @base, string head, string title)
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;

        (await Create(host, alice, new { opKey = Key(), @base, head, title, body = "", draft = false })).AssertStatus(HttpStatusCode.BadRequest);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task A_branch_that_does_not_exist_is_refused_before_GitHub_is_asked()
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;

        var response = await Create(host, alice, new { opKey = Key(), @base = "main", head = "nowhere", title = "x", body = "", draft = false });

        response.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("branch-not-found", await response.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    /// <summary>S1 holds for a route with no pull request in it: signed out, nothing is opened.</summary>
    [Fact]
    public async Task Signed_out_nothing_is_opened()
    {
        var w = Build();
        var (host, writer, _, _) = await StartAsync(w);
        await using var __ = host;

        (await Create(host, null, new { opKey = Key(), @base = "main", head = "feature", title = "x", body = "", draft = false })).AssertStatus(HttpStatusCode.Unauthorized);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task The_template_is_read_from_the_base_branch_wherever_GitHub_would_look()
    {
        var with = Build("## What changed\n\n## How it was tested\n");
        var (host, _, alice, _) = await StartAsync(with);
        await using (host)
        {
            var found = await Json(await host.GetAsync("/api/repos/acme/plant/pulls/template?base=main", alice));
            Assert.Equal("## What changed\n\n## How it was tested\n", found.GetProperty("markdown").GetString());
        }

        var without = Build();
        var (other, _, alice2, _) = await StartAsync(without);
        await using (other)
        {
            var none = await Json(await other.GetAsync("/api/repos/acme/plant/pulls/template?base=main", alice2));
            Assert.Equal(JsonValueKind.Null, none.GetProperty("markdown").ValueKind);
        }
    }

    [Fact]
    public async Task The_list_says_whether_a_new_one_can_be_opened_here()
    {
        var w = Build();
        var (host, _, alice, _) = await StartAsync(w);
        await using var __ = host;

        Assert.True((await Json(await host.GetAsync("/api/repos/acme/plant/pulls", alice))).GetProperty("writable").GetBoolean());
        Assert.False((await Json(await host.GetAsync("/api/repos/acme/plant/pulls", null))).GetProperty("writable").GetBoolean());
    }

    // ---- editing --------------------------------------------------------------------------------

    [Fact]
    public async Task The_title_and_description_are_edited_from_what_the_person_saw()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;

        // The description as the page has it: Versio's own tags stripped, which the comparison allows for.
        var response = await Post(host, alice, token, "/edit", new { opKey = Key(), title = "Fix the pump", expectTitle = "Fix", body = "Why, at last.", expectBody = "Why" });

        response.AssertStatus(HttpStatusCode.OK);
        var pull = w.Repo.Pulls.Single(p => p.Info.Number == 12);
        Assert.Equal(("Fix the pump", "Why, at last."), (pull.Info.Title, pull.Body));
        Assert.Equal("edit-pull", writer.Writes.Single().Action);
    }

    /// <summary>
    /// GitHub's edit overwrites whatever is there. Someone else changed the description after the
    /// person started editing: the edit is refused, their text kept by the page, and theirs survives.
    /// </summary>
    [Fact]
    public async Task An_edit_of_text_someone_else_changed_since_is_refused_and_nothing_is_overwritten()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;
        w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { Title = "Fix, renamed by jo" }, "Jo's new description");

        var body = await Post(host, alice, token, "/edit", new { opKey = Key(), body = "Mine", expectBody = "Why" });
        var title = await Post(host, alice, token, "/edit", new { opKey = Key(), title = "Mine", expectTitle = "Fix" });

        body.AssertStatus(HttpStatusCode.Conflict);
        title.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("changed-since", await body.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.Equal("Jo's new description", w.Repo.Pulls[0].Body);
    }

    [Fact]
    public async Task An_edit_that_does_not_say_what_it_replaces_is_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/edit", new { opKey = Key(), body = "Mine" })).AssertStatus(HttpStatusCode.BadRequest);
        (await Post(host, alice, token, "/edit", new { opKey = Key(), title = "Mine" })).AssertStatus(HttpStatusCode.BadRequest);
        Assert.Empty(writer.Writes);
    }

    /// <summary>A base change makes the review's own snapshot stale (C1): the page is told, as for a push.</summary>
    [Fact]
    public async Task A_base_change_is_made_and_the_review_then_says_it_changed()
    {
        var w = Build();
        var (host, _, alice, token) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/edit", new { opKey = Key(), @base = "release" })).AssertStatus(HttpStatusCode.OK);
        var status = await Json(await host.GetAsync("/api/repos/acme/plant/pulls/12/status", alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token }));

        Assert.Equal("release", w.Repo.Pulls[0].Info.BaseRef);
        Assert.True(status.GetProperty("stale").GetBoolean());
        Assert.True(status.GetProperty("baseMoved").GetBoolean());
    }

    [Fact]
    public async Task A_base_someone_else_already_changed_is_not_changed_again_and_a_missing_one_is_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/edit", new { opKey = Key(), @base = "nowhere" })).AssertStatus(HttpStatusCode.UnprocessableEntity);
        w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { BaseRef = "release" }, w.Repo.Pulls[0].Body);
        (await Post(host, alice, token, "/edit", new { opKey = Key(), @base = "main" })).AssertStatus(HttpStatusCode.Conflict);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task A_lost_answer_to_an_edit_is_settled_by_reading_the_pull_request()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);

        var response = await Post(host, alice, token, "/edit", new { opKey = Key(), title = "Fix the pump", expectTitle = "Fix" });

        response.AssertStatus(HttpStatusCode.OK);
        Assert.True((await Json(response)).GetProperty("reconciled").GetBoolean());
        Assert.Single(writer.Writes);
    }

    // ---- state ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_pull_request_is_closed_and_reopened_and_a_merged_one_is_left_alone()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/state", new { opKey = Key(), open = false })).AssertStatus(HttpStatusCode.OK);
        Assert.Equal("CLOSED", w.Repo.PullStates[12]);
        (await Post(host, alice, token, "/state", new { opKey = Key(), open = true })).AssertStatus(HttpStatusCode.OK);
        Assert.False(w.Repo.PullStates.ContainsKey(12));

        w.Repo.PullStates[12] = "MERGED";
        (await Post(host, alice, token, "/state", new { opKey = Key(), open = false })).AssertStatus(HttpStatusCode.Conflict);
        Assert.Equal(["close", "reopen"], writer.Writes.Select(c => c.Action));
    }

    [Fact]
    public async Task An_open_pull_request_becomes_a_draft_and_ready_again_and_a_closed_one_does_not()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/draft", new { opKey = Key(), draft = true })).AssertStatus(HttpStatusCode.OK);
        Assert.True(w.Repo.Pulls[0].Info.Draft);
        (await Post(host, alice, token, "/draft", new { opKey = Key(), draft = false })).AssertStatus(HttpStatusCode.OK);
        Assert.False(w.Repo.Pulls[0].Info.Draft);

        w.Repo.PullStates[12] = "CLOSED";
        (await Post(host, alice, token, "/draft", new { opKey = Key(), draft = true })).AssertStatus(HttpStatusCode.Conflict);
        Assert.Equal(["to-draft", "ready"], writer.Writes.Select(c => c.Action));
    }

    // ---- labels and assignees -------------------------------------------------------------------

    [Fact]
    public async Task Labels_the_repository_has_are_put_on_and_taken_off_and_none_is_made()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/labels", new { opKey = Key(), add = new[] { "safety" }, remove = new[] { "plc" } })).AssertStatus(HttpStatusCode.OK);
        Assert.Equal(["safety"], w.Repo.PullPeople[12].Labels.Select(l => l.Name));

        var unknown = await Post(host, alice, token, "/labels", new { opKey = Key(), add = new[] { "made-up" } });
        unknown.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("label-not-found", await unknown.Body(), StringComparison.Ordinal);
        Assert.Single(writer.Writes);
    }

    /// <summary>GitHub answers success while dropping an assignee who cannot be assigned: asked first, so the page is never told it worked.</summary>
    [Fact]
    public async Task Only_someone_who_can_be_assigned_is_and_anyone_else_is_refused_with_why()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var __ = host;

        (await Post(host, alice, token, "/assignees", new { opKey = Key(), add = new[] { "sam" } })).AssertStatus(HttpStatusCode.OK);
        var refused = await Post(host, alice, token, "/assignees", new { opKey = Key(), add = new[] { "mallory" } });
        (await Post(host, alice, token, "/assignees", new { opKey = Key(), add = new[] { "not a login" } })).AssertStatus(HttpStatusCode.BadRequest);

        refused.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("not-assignable", await refused.Body(), StringComparison.Ordinal);
        Assert.Equal(["sam"], w.Repo.Assignees[12]);
        Assert.Single(writer.Writes);
    }

    // ---- what the page reads --------------------------------------------------------------------

    [Fact]
    public async Task Status_gives_the_live_title_and_description_without_Versios_tags_and_whether_the_viewer_may_edit()
    {
        var w = Build();
        var (host, _, alice, token) = await StartAsync(w);
        await using var __ = host;
        w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { Title = "Fix <b>" }, w.Repo.Pulls[0].Body);

        var summary = (await Json(await host.GetAsync("/api/repos/acme/plant/pulls/12/status", alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token }))).GetProperty("summary");

        Assert.Equal("Fix <b>", summary.GetProperty("titleText").GetString());
        Assert.Equal("Why", summary.GetProperty("markdown").GetString());
        Assert.True(summary.GetProperty("viewerCanUpdate").GetBoolean());
        Assert.Equal("plc", summary.GetProperty("labels")[0].GetProperty("value").GetString());

        w.Repo.CannotUpdate.Add("alice-token");
        var refused = (await Json(await host.GetAsync("/api/repos/acme/plant/pulls/12/status", alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token }))).GetProperty("summary");
        Assert.False(refused.GetProperty("viewerCanUpdate").GetBoolean());
    }

    [Fact]
    public async Task The_repositorys_labels_are_listed_by_their_real_names()
    {
        var w = Build();
        var (host, _, alice, _) = await StartAsync(w);
        await using var __ = host;

        var labels = (await Json(await host.GetAsync("/api/repos/acme/plant/labels", alice))).GetProperty("labels");

        Assert.Equal(["safety", "plc"], labels.EnumerateArray().Select(l => l.GetProperty("value").GetString()));
    }
}
