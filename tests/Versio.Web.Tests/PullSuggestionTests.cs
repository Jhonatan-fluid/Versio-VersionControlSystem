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
/// Applying suggested changes (docs/web/FINISH.md, Step 5): one commit on the reviewed head with
/// GitHub's atomic precondition, only where a suggestion's thread still is, only with the merge
/// tier, and a lost answer settled by the commit's own tag.
/// </summary>
public sealed class PullSuggestionTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private const string Before = "PROGRAM Pump\nVAR\n  run : BOOL;\n  stop : BOOL;\nEND_VAR\n";

    private sealed record World(FakeGitHub GitHub, FakeGitHub.FakeRepo Repo, string Head);

    private static ReviewThread Thread(string id, string commentId, string body, int line, int? startLine = null, string side = "RIGHT", bool outdated = false, string author = "sam") =>
        // An outdated thread keeps a line here on purpose: the refusal must come from "outdated", not from a missing line.
        new(id, "src/Pump.st", false, outdated, line, startLine, side, line, "LINE",
            [new ReviewComment(commentId, author, body, When, null, null)], false, true, true, false);

    /// <param name="pump">The reviewed file's bytes, where a test needs other than <see cref="Before"/> (the fake's blobs are keyed by content at commit time).</param>
    private static World Build(byte[]? pump = null)
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var start = github.Commit(repo, "start", new Dictionary<string, byte[]> { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\nVAR\nEND_VAR\n") });
        github.Commit(repo, "main", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal));
        var head = github.Commit(repo, "fix", new Dictionary<string, byte[]> { ["src/Pump.st"] = pump ?? Encoding.UTF8.GetBytes(Before) });
        repo.MergeBases[(repo.Refs["main"], head)] = start;
        repo.Pulls.Add((new PullInfo(12, "Fix", "jo", When, "main", "fix", head, false), "Why"));
        repo.PullFiles[12] = new PullFileList([new PullFile("src/Pump.st", "modified", null, "@@ -2,1 +2,3 @@\n VAR\n+  run : BOOL;\n+  stop : BOOL;")], false);
        repo.Threads[12] = new ReviewThreadList(
        [
            Thread("T_run", "C_run", "Default it.\n```suggestion\n  run : BOOL := TRUE;\n```", 3),
            Thread("T_stop", "C_stop", "```suggestion\n  stop : BOOL := FALSE;\n```", 4),
            Thread("T_both", "C_both", "```suggestion\n  run, stop : BOOL;\n```", 4, startLine: 3),
            Thread("T_old", "C_old", "```suggestion\nx\n```", 3, outdated: true),
            Thread("T_left", "C_left", "```suggestion\nx\n```", 3, side: "LEFT"),
            Thread("T_plain", "C_plain", "No suggestion here.", 3),
            Thread("T_first", "C_first", "```suggestion\nPROGRAM Pump // the pump\n```", 1),
        ], false);
        return new World(github, repo, head);
    }

    private static Dictionary<string, string?> Settings(bool merge) => new()
    {
        ["Web:Writes:Enabled"] = "true",
        ["Web:Writes:Repositories:0"] = "acme/plant",
        ["Web:Writes:SingleInstance"] = "true",
        ["Web:Writes:Merge"] = merge ? "true" : "false",
    };

    private static async Task<(TestHost Host, FakeWriter Writer, string Alice, string Token)> StartAsync(World w, bool merge = true)
    {
        var writer = new FakeWriter { Repo = w.Repo };
        var host = await TestHost.StartAsync(w.GitHub, Settings(merge), s => s.AddSingleton<IRepoWriter>(writer));
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6));
        var alice = $"{AccessResolver.SessionCookie}={session.Id}";
        var snapshot = await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice);
        return (host, writer, alice, JsonDocument.Parse(await snapshot.Body()).RootElement.GetProperty("token").GetString()!);
    }

    private static string Key() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> Apply(TestHost host, string alice, string token, params string[] comments) =>
        host.SendAsync(HttpMethod.Post, "/api/repos/acme/plant/pulls/12/suggestions/apply", alice,
            new Dictionary<string, string> { [AuthEndpoints.CsrfHeader] = "1", [PullEndpoints.SnapshotHeader] = token, ["Sec-Fetch-Site"] = "same-origin" },
            JsonSerializer.Serialize(new { opKey = Key(), comments }));

    private static string FileAfter(World w) => Encoding.UTF8.GetString(w.Repo.Commits[w.Repo.Refs["fix"]]["src/Pump.st"]);

    [Fact]
    public async Task A_suggestion_is_committed_on_the_reviewed_head_with_its_author_credited_and_the_tag_in_the_message()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        var response = await Apply(host, alice, token, "C_run");

        response.AssertStatus(HttpStatusCode.OK);
        var commit = writer.Commits.Single();
        Assert.Equal(("fix", w.Head), (commit.Branch, commit.Expected));
        Assert.StartsWith("Apply suggestion from code review\n\nCo-authored-by: sam <sam@users.noreply.github.com>\n\n<!-- versio:op v1 ", commit.Message, StringComparison.Ordinal);
        Assert.Equal("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\n  stop : BOOL;\nEND_VAR\n", FileAfter(w));
        Assert.Equal("alice", writer.Writes.Single().Access.Who);
    }

    /// <summary>
    /// A file that is not UTF-8 is not rewritten: decoding it would put U+FFFD in place of every
    /// bad byte and the commit would change the whole file (review, 2026-10-24). A BOM is fine.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_UTF8_text_takes_no_suggestion_and_one_with_a_BOM_does()
    {
        // A Windows-1252 degree sign in a comment on the first line; the suggested line is untouched.
        var latin = Encoding.UTF8.GetBytes("PROGRAM Pump // ").Concat([(byte)0xB0]).Concat(Encoding.UTF8.GetBytes("\nVAR\n  run : BOOL;\n  stop : BOOL;\nEND_VAR\n")).ToArray();
        var w = Build(latin);
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        var refused = await Apply(host, alice, token, "C_run");

        refused.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("file-not-utf8", JsonDocument.Parse(await refused.Body()).RootElement.GetProperty("error").GetString());
        Assert.Empty(writer.Commits);
        Assert.Equal(latin, w.Repo.Commits[w.Repo.Refs["fix"]]["src/Pump.st"]);

        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Before)).ToArray();
        var w2 = Build(bom);
        var (host2, writer2, alice2, token2) = await StartAsync(w2);
        await using var __ = host2;

        (await Apply(host2, alice2, token2, "C_run")).AssertStatus(HttpStatusCode.OK);

        var after = w2.Repo.Commits[w2.Repo.Refs["fix"]]["src/Pump.st"];
        Assert.Equal(Encoding.UTF8.GetPreamble(), after.Take(3));
        Assert.Equal("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\n  stop : BOOL;\nEND_VAR\n", Encoding.UTF8.GetString(after, 3, after.Length - 3));
        Assert.Single(writer2.Commits);

        // A suggestion that replaces line 1 keeps the BOM too: it is not part of the line (review
        // of the review, 2026-10-24 — the first fix dropped it with the line).
        var w3 = Build(bom);
        var (host3, _, alice3, token3) = await StartAsync(w3);
        await using var ___ = host3;
        (await Apply(host3, alice3, token3, "C_first")).AssertStatus(HttpStatusCode.OK);
        var first = w3.Repo.Commits[w3.Repo.Refs["fix"]]["src/Pump.st"];
        Assert.Equal(Encoding.UTF8.GetPreamble(), first.Take(3));
        Assert.StartsWith("PROGRAM Pump // the pump\nVAR\n", Encoding.UTF8.GetString(first, 3, first.Length - 3), StringComparison.Ordinal);
    }

    /// <summary>A batch is one commit; applied bottom-up so the first edit never shifts the second's lines.</summary>
    [Fact]
    public async Task A_batch_of_suggestions_on_one_file_is_one_commit_and_overlapping_ones_are_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Apply(host, alice, token, "C_run", "C_both")).AssertStatus(HttpStatusCode.UnprocessableEntity);
        (await Apply(host, alice, token, "C_run", "C_stop")).AssertStatus(HttpStatusCode.OK);

        Assert.Single(writer.Commits);
        Assert.Equal("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\n  stop : BOOL := FALSE;\nEND_VAR\n", FileAfter(w));
        Assert.StartsWith("Apply suggestions from code review", writer.Commits.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suggestion_whose_lines_moved_or_on_the_old_side_or_absent_or_elsewhere_applies_nothing()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        (await Apply(host, alice, token, "C_old")).AssertStatus(HttpStatusCode.UnprocessableEntity);
        (await Apply(host, alice, token, "C_left")).AssertStatus(HttpStatusCode.UnprocessableEntity);
        (await Apply(host, alice, token, "C_plain")).AssertStatus(HttpStatusCode.UnprocessableEntity);
        (await Apply(host, alice, token, "C_nowhere")).AssertStatus(HttpStatusCode.NotFound);
        (await Apply(host, alice, token)).AssertStatus(HttpStatusCode.BadRequest);

        Assert.Empty(writer.Commits);
        Assert.Equal(Before, FileAfter(w));
    }

    [Fact]
    public async Task Without_the_merge_tier_the_route_does_not_exist()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w, merge: false);
        await using var _ = host;

        (await Apply(host, alice, token, "C_run")).AssertStatus(HttpStatusCode.NotFound);
        Assert.Empty(writer.Calls);
    }

    /// <summary>A push after the review opened: nothing is applied over it, by the server's check and by GitHub's precondition alike.</summary>
    [Fact]
    public async Task A_head_that_moved_applies_nothing_before_the_check_and_after_it()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        // A push: the branch moves, and so does the pull request's head.
        writer.BeforeEffect = () =>
        {
            w.Repo.Refs["fix"] = new string('9', 40);
            w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { HeadSha = new string('9', 40) }, "Why");
        };

        var landedAfterChecks = await Apply(host, alice, token, "C_run");
        writer.BeforeEffect = null;
        var landedBefore = await Apply(host, alice, token, "C_stop");

        landedAfterChecks.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("github-refused", await landedAfterChecks.Body(), StringComparison.Ordinal);
        landedBefore.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("snapshot-stale", await landedBefore.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Commits);
    }

    /// <summary>The answer lost after GitHub committed: the live head is this operation's commit — made on the reviewed head, carrying the tag — so it is done, not done again.</summary>
    [Fact]
    public async Task A_lost_answer_is_settled_by_the_commit_on_the_reviewed_head_that_carries_the_tag()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);

        var response = await Apply(host, alice, token, "C_run");

        response.AssertStatus(HttpStatusCode.OK);
        var answer = JsonDocument.Parse(await response.Body()).RootElement;
        Assert.True(answer.GetProperty("reconciled").GetBoolean());
        Assert.Equal(w.Repo.Refs["fix"], answer.GetProperty("id").GetString());
        Assert.Single(writer.Commits);
    }

    [Fact]
    public async Task The_threads_read_names_each_comments_suggestion_for_the_page_to_draw()
    {
        var w = Build();
        var (host, _, alice, token) = await StartAsync(w);
        await using var _ = host;

        var threads = JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/pulls/12/threads", alice, new Dictionary<string, string> { [PullEndpoints.SnapshotHeader] = token })).Body()).RootElement.GetProperty("threads");
        var run = threads.EnumerateArray().First(t => t.GetProperty("id").GetString() == "T_run").GetProperty("comments")[0];
        var plain = threads.EnumerateArray().First(t => t.GetProperty("id").GetString() == "T_plain").GetProperty("comments")[0];

        Assert.Equal("  run : BOOL := TRUE;\n", run.GetProperty("suggestion").GetString());
        // The prose alone is rendered: the fence is the page's widget, not a second code block.
        Assert.Contains("Default it.", run.GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TRUE", run.GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("suggestion").ValueKind);
    }
}
