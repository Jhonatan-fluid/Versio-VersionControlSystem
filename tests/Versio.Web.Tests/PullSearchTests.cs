using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Auth;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// GitHub's pull request list (docs/web/FINISH.md, B10): a signed-in person searches with GitHub's
/// grammar and gets the counts and a page; a reader without a token gets the list by state and is
/// told the counts are of what was listed; a token outside the grammar is refused by name.
/// </summary>
public sealed class PullSearchTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static FakeGitHub Build(out FakeGitHub.FakeRepo repo)
    {
        var github = new FakeGitHub();
        repo = github.Add("acme", "plant");
        github.Commit(repo, "main", FakeGitHub.FromDisk("README.md"));
        for (var i = 1; i <= 3; i++)
        {
            repo.Pulls.Add((new PullInfo(i, $"Change {i}", i == 2 ? "sam" : "jo", When.AddHours(i), "main", $"b{i}", new string((char)('a' + i), 40), i == 3), "Why"));
        }

        repo.PullStates[3] = "MERGED";
        repo.PullPeople[1] = ([new PullLabel("plc", "0e8a16")], [new PullReviewer("kim", "APPROVED")]);
        repo.Assignees[2] = ["sam"];
        return github;
    }

    private static async Task<string> SignInAsync(TestHost host)
    {
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6));
        return $"{AccessResolver.SessionCookie}={session.Id}";
    }

    private static async Task<JsonElement> Get(TestHost host, string path, string? cookie = null) =>
        JsonDocument.Parse(await (await host.GetAsync(path, cookie)).Body()).RootElement;

    private static IEnumerable<int> Numbers(JsonElement list) => list.GetProperty("pulls").EnumerateArray().Select(p => p.GetProperty("number").GetInt32());

    [Fact]
    public async Task A_signed_in_person_searches_with_GitHubs_grammar_and_gets_the_counts_and_the_rows_detail()
    {
        await using var host = await TestHost.StartAsync(Build(out _));
        var alice = await SignInAsync(host);

        var open = await Get(host, "/api/repos/acme/plant/pulls", alice);
        var byAuthor = await Get(host, "/api/repos/acme/plant/pulls?q=" + Uri.EscapeDataString("is:open author:sam"), alice);
        var closed = await Get(host, "/api/repos/acme/plant/pulls?q=" + Uri.EscapeDataString("is:closed"), alice);
        var approved = await Get(host, "/api/repos/acme/plant/pulls?q=" + Uri.EscapeDataString("review:approved label:plc"), alice);

        Assert.True(open.GetProperty("searched").GetBoolean());
        Assert.Equal([2, 1], Numbers(open));
        Assert.Equal((2, 2, 1), (open.GetProperty("total").GetInt32(), open.GetProperty("open").GetInt32(), open.GetProperty("closed").GetInt32()));
        Assert.Equal("is:open sort:updated-desc", open.GetProperty("query").GetString());
        Assert.Equal([2], Numbers(byAuthor));
        Assert.Equal([3], Numbers(closed));
        Assert.Equal("MERGED", closed.GetProperty("pulls")[0].GetProperty("state").GetString());
        Assert.Equal([1], Numbers(approved));
        var row = approved.GetProperty("pulls")[0];
        Assert.Equal(("APPROVED", "plc"), (row.GetProperty("reviewDecision").GetString(), row.GetProperty("labels")[0].GetProperty("name").GetString()));
        Assert.Equal("sam", byAuthor.GetProperty("pulls")[0].GetProperty("assignees")[0].GetProperty("login").GetString());
    }

    [Fact]
    public async Task Without_a_token_the_list_is_by_state_and_says_it_did_not_search()
    {
        await using var host = await TestHost.StartAsync(Build(out _));

        var open = await Get(host, "/api/repos/acme/plant/pulls?q=" + Uri.EscapeDataString("author:sam"));
        var closed = await Get(host, "/api/repos/acme/plant/pulls?q=" + Uri.EscapeDataString("is:closed"));

        Assert.False(open.GetProperty("searched").GetBoolean());
        // The author filter is not applied without a search: both open ones come back, the page filters what it got.
        Assert.Equal([2, 1], Numbers(open));
        Assert.Equal(JsonValueKind.Null, open.GetProperty("closed").ValueKind);
        Assert.Equal([3], Numbers(closed));
    }

    [Fact]
    public async Task A_token_outside_the_grammar_is_refused_by_name_and_nothing_is_asked()
    {
        var github = Build(out _);
        await using var host = await TestHost.StartAsync(github);
        var alice = await SignInAsync(host);

        var response = await host.GetAsync("/api/repos/acme/plant/pulls?q=" + Uri.EscapeDataString("repo:other/thing is:open"), alice);

        response.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Contains("repo:", await response.Body(), StringComparison.Ordinal);
        Assert.DoesNotContain(github.Reads, r => r.What.StartsWith("search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_past_the_first_follows_the_cursor_and_a_malformed_cursor_is_not_found()
    {
        var github = Build(out var repo);
        for (var i = 4; i <= 60; i++)
        {
            repo.Pulls.Add((new PullInfo(i, $"Change {i}", "jo", When.AddHours(i), "main", $"b{i}", new string('e', 40), false), "Why"));
        }

        await using var host = await TestHost.StartAsync(github);
        var alice = await SignInAsync(host);

        var first = await Get(host, "/api/repos/acme/plant/pulls", alice);
        var next = first.GetProperty("next").GetString()!;
        var second = await Get(host, "/api/repos/acme/plant/pulls?after=" + Uri.EscapeDataString(next), alice);

        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.Equal(50, Numbers(first).Count());
        Assert.Equal(9, Numbers(second).Count());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);
        (await host.GetAsync("/api/repos/acme/plant/pulls?after=" + Uri.EscapeDataString("not a cursor!"), alice)).AssertStatus(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Without a token the pages are REST's, in units of the page size. Asking for one row more
    /// than shown at page N showed 50 and moved on to page N+1 of 51, so every 51st pull request
    /// was never listed (the 2026-10-24 review's item 35).
    /// </summary>
    [Fact]
    public async Task Anonymous_pages_follow_each_other_without_a_gap()
    {
        var github = Build(out var repo);
        for (var i = 4; i <= 102; i++)
        {
            repo.Pulls.Add((new PullInfo(i, $"Change {i}", "jo", When.AddHours(i), "main", $"b{i}", new string('e', 40), false), "Why"));
        }

        // 101 open (pull 3 is merged), newest first: 102 … 53 | 52 … 4, 2 | 1.
        await using var host = await TestHost.StartAsync(github);

        var first = await Get(host, "/api/repos/acme/plant/pulls");
        var second = await Get(host, "/api/repos/acme/plant/pulls?page=2");
        var third = await Get(host, "/api/repos/acme/plant/pulls?page=3");

        Assert.Equal(50, Numbers(first).Count());
        Assert.Equal("2", first.GetProperty("next").GetString());
        Assert.Equal(52, Numbers(second).First());
        Assert.Equal(50, Numbers(second).Count());
        Assert.Equal("3", second.GetProperty("next").GetString());
        Assert.Equal([1], Numbers(third));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("next").ValueKind);
        Assert.False(third.GetProperty("truncated").GetBoolean());
    }
}
