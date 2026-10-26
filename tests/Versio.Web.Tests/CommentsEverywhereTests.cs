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
/// The server side of "comments everywhere" (docs/web/FINISH.md, Step 3b): the annotations a check
/// run left, read for the Checks tab and the file sections; and a commit's comments, read under
/// the commit page and added in the person's name with the same key rules as every write — with
/// no snapshot, because a commit is its own revision.
/// </summary>
public sealed class CommentsEverywhereTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

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
        repo.Checks[head] = [new CheckInfo("plc-compile", "completed", "failure", "https://ci.example/2", "check-run") { Id = 2, AnnotationCount = 2 }];
        repo.Annotations[2] =
        [
            new CheckAnnotation("src/Pump.st", 2, 2, "failure", "unknown identifier <b>tSetpoint</b>", "C0046"),
            new CheckAnnotation("src/Pump.st", 1, 3, "warning", "unused variable", null),
        ];
        repo.CommitComments[head] =
        [
            new CommitComment("CC_1", "sam", null, "Nice <script>x</script>", When, null, null, "https://github.com/acme/plant/commit/x#commitcomment-1"),
            new CommitComment("CC_2", "lee", null, "On a line", When.AddMinutes(1), "src/Pump.st", 2, null),
        ];
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

    private static Dictionary<string, string> Headers() => new() { [AuthEndpoints.CsrfHeader] = "1", ["Sec-Fetch-Site"] = "same-origin" };

    private static Task<HttpResponseMessage> Comment(TestHost host, string alice, string sha, object body) =>
        host.SendAsync(HttpMethod.Post, $"/api/repos/acme/plant/commits/{sha}/comments", alice, Headers(), JsonSerializer.Serialize(body));

    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Body()).RootElement;

    // ---- check annotations --------------------------------------------------------------------------

    [Fact]
    public async Task A_runs_annotations_are_listed_with_their_lines_and_level_and_the_CIs_words_spelled()
    {
        var w = Build();
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var read = await host.GetAsync("/api/repos/acme/plant/pulls/12/checks/2/annotations", alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token });
        var none = await host.GetAsync("/api/repos/acme/plant/pulls/12/checks/9/annotations", alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token });

        read.AssertStatus(HttpStatusCode.OK);
        var list = (await Json(read)).GetProperty("annotations").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(("src/Pump.st", 2, 2, "failure", "C0046"), (list[0].GetProperty("path").GetString(), list[0].GetProperty("startLine").GetInt32(), list[0].GetProperty("endLine").GetInt32(), list[0].GetProperty("level").GetString(), list[0].GetProperty("title").GetString()));
        // The message is the CI's words as text (hidden characters spelled out): the page draws it
        // as text, so the tags are letters on the screen, never markup.
        Assert.Equal("unknown identifier <b>tSetpoint</b>", list[0].GetProperty("message").GetString());
        Assert.Equal((1, 3, "warning"), (list[1].GetProperty("startLine").GetInt32(), list[1].GetProperty("endLine").GetInt32(), list[1].GetProperty("level").GetString()));
        Assert.Equal(JsonValueKind.Null, list[1].GetProperty("title").ValueKind);
        // A run GitHub knows nothing of: an empty list, not an error — the ids come from the status.
        none.AssertStatus(HttpStatusCode.OK);
        Assert.Empty((await Json(none)).GetProperty("annotations").EnumerateArray());
    }

    // ---- commit comments ------------------------------------------------------------------------------

    [Fact]
    public async Task A_commits_comments_are_listed_oldest_first_naming_a_line_comments_file_and_a_bad_sha_is_not_found()
    {
        var w = Build();
        var (host, _, alice, _) = await StartAsync(w);
        await using var __ = host;

        var read = await host.GetAsync($"/api/repos/acme/plant/commit/comments?sha={w.Head}", alice);
        var anonymous = await host.GetAsync($"/api/repos/acme/plant/commit/comments?sha={w.Head}");
        var odd = await host.GetAsync("/api/repos/acme/plant/commit/comments?sha=../x");

        read.AssertStatus(HttpStatusCode.OK);
        var body = await Json(read);
        Assert.True(body.GetProperty("writable").GetBoolean());
        var list = body.GetProperty("comments").EnumerateArray().ToList();
        Assert.Equal(["CC_1", "CC_2"], list.Select(c => c.GetProperty("id").GetString()));
        // The body is Markdown rendered by Core, so the script is text; the page sanitises again.
        Assert.DoesNotContain("<script>", list[0].GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.Equal(("src/Pump.st", 2), (list[1].GetProperty("path").GetString(), list[1].GetProperty("line").GetInt32()));
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("path").ValueKind);
        // Writes are on, but nobody is signed in: the list is readable, the box is not drawn.
        anonymous.AssertStatus(HttpStatusCode.OK);
        Assert.False((await Json(anonymous)).GetProperty("writable").GetBoolean());
        odd.AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_commit_comment_is_posted_in_the_persons_name_with_its_tag_and_a_bad_sha_body_or_unknown_commit_is_refused()
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;
        var key = Key();

        var posted = await Comment(host, alice, w.Head, new { opKey = key, body = "Looks right." });
        var again = await Comment(host, alice, w.Head, new { opKey = key, body = "Looks right." });
        var shortSha = await Comment(host, alice, w.Head[..7], new { opKey = Key(), body = "x" });
        var empty = await Comment(host, alice, w.Head, new { opKey = Key(), body = "  " });
        var unknown = await Comment(host, alice, new string('9', 40), new { opKey = Key(), body = "x" });

        posted.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("done", (await Json(posted)).GetProperty("outcome").GetString());
        var call = Assert.Single(writer.Writes);
        Assert.Equal(("commit-comment", "alice"), (call.Action, call.Access.Who));
        Assert.StartsWith(w.Head + " Looks right.", call.Detail, StringComparison.Ordinal);
        Assert.Contains("<!-- versio:op v1 " + key, call.Detail, StringComparison.Ordinal);
        // The same key again is the same operation: answered from the ledger, sent once.
        again.AssertStatus(HttpStatusCode.OK);
        Assert.Single(writer.Writes);
        shortSha.AssertStatus(HttpStatusCode.BadRequest);
        empty.AssertStatus(HttpStatusCode.BadRequest);
        unknown.AssertStatus(HttpStatusCode.NotFound);
        // Filed where the read finds it: the commit page shows it after a reload.
        var read = await Json(await host.GetAsync($"/api/repos/acme/plant/commit/comments?sha={w.Head}", alice));
        Assert.Equal(3, read.GetProperty("comments").GetArrayLength());
    }

    /// <summary>A lost answer is settled by finding the viewer's tagged comment on the commit; never posted twice.</summary>
    [Fact]
    public async Task A_lost_answer_to_a_commit_comment_is_settled_by_its_tag()
    {
        var w = Build();
        var (host, writer, alice, _) = await StartAsync(w);
        await using var __ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);

        var response = await Comment(host, alice, w.Head, new { opKey = Key(), body = "Settled?" });

        response.AssertStatus(HttpStatusCode.OK);
        var body = await Json(response);
        Assert.Equal("done", body.GetProperty("outcome").GetString());
        Assert.True(body.GetProperty("reconciled").GetBoolean());
        Assert.Single(writer.Writes);
    }
}
