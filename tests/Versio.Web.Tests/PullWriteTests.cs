using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Api;
using Versio.Web.Auth;
using Versio.Web.Review;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Pull request review, write side (docs/web/PLAN.md, *Pull request review*, slice 2): writes in
/// the person's own name only, behind the kill switch and the forgery checks, reserved so a
/// duplicate is one write, and reconciled — never retried blindly — when the outcome is unknown.
/// </summary>
public sealed class PullWriteTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    private const string Patch = "@@ -3 +3 @@\n-  run : BOOL;\n+  run : BOOL := TRUE;";

    private static readonly Dictionary<string, string?> WritesOn = new()
    {
        ["Web:Writes:Enabled"] = "true",
        ["Web:Writes:Repositories:0"] = "acme/plant",
        ["Web:Writes:SingleInstance"] = "true",
    };

    private sealed record World(FakeGitHub GitHub, FakeGitHub.FakeRepo Repo, string Head, string MainTip, string Start);

    private static World Build()
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var start = github.Commit(repo, "start", new Dictionary<string, byte[]>
        {
            ["src/Pump.st"] = B("PROGRAM Pump\nVAR\n  run : BOOL;\nEND_VAR\n"),
        });
        var mainTip = github.Commit(repo, "main", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal) { ["src/Later.st"] = B("x") });
        var head = github.Commit(repo, "fix", new Dictionary<string, byte[]>(repo.Commits[start], StringComparer.Ordinal)
        {
            ["src/Pump.st"] = B("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\nEND_VAR\n"),
            ["src/Logo.bin"] = [0, 1, 2],
            ["src/Two.st"] = B("a\nB\n...\nx\nY\n"),
        });
        repo.MergeBases[(mainTip, head)] = start;
        repo.Pulls.Add((new PullInfo(12, "Fix interlock", "jo", When, "main", "fix", head, false), "Why"));
        repo.PullFiles[12] = new PullFileList(
        [
            new PullFile("src/Logo.bin", "added", null, null),
            new PullFile("src/Pump.st", "modified", null, Patch),
            new PullFile("src/Two.st", "added", null, "@@ -1,2 +1,2 @@\n a\n-b\n+B\n@@ -10,2 +10,2 @@\n x\n-y\n+Y"),
        ], false);
        return new World(github, repo, head, mainTip, start);
    }

    private static async Task<(TestHost Host, FakeWriter Writer, string Alice, string Token)> StartAsync(World w, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? more = null)
    {
        var writer = new FakeWriter();
        writer.Threads["T_here1234"] = ("acme/plant", 12, false);
        writer.Threads["T_elsewhere"] = ("acme/other", 12, false);
        var host = await TestHost.StartAsync(w.GitHub, settings ?? WritesOn, s =>
        {
            s.AddSingleton<IRepoWriter>(writer);
            more?.Invoke(s);
        });
        var alice = SignIn(host);
        var snapshot = await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice);
        snapshot.AssertStatus(HttpStatusCode.OK);
        var token = JsonDocument.Parse(await snapshot.Body()).RootElement.GetProperty("token").GetString()!;
        return (host, writer, alice, token);
    }

    private static string SignIn(TestHost host)
    {
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6)).GetAwaiter().GetResult();
        return $"{AccessResolver.SessionCookie}={session.Id}";
    }

    private static string Key(DateTimeOffset? at = null) =>
        $"{(at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

    private static Dictionary<string, string> Headers(string token) => new()
    {
        [AuthEndpoints.CsrfHeader] = "1",
        [PullEndpoints.SnapshotHeader] = token,
        ["Sec-Fetch-Site"] = "same-origin",
    };

    private static Task<HttpResponseMessage> Post(TestHost host, string path, object body, string? cookie, Dictionary<string, string> headers, string method = "POST") =>
        host.SendAsync(new HttpMethod(method), "/api/repos/acme/plant/pulls/12" + path, cookie, headers, JsonSerializer.Serialize(body));

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    // ---- the rules every write shares -----------------------------------------------------------

    /// <summary>S9: off by default, and off for any repository not opted in, as "not found".</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_writes_off_or_the_repository_not_opted_in_every_write_route_is_not_found(bool enabledElsewhere)
    {
        var settings = enabledElsewhere
            ? new Dictionary<string, string?> { ["Web:Writes:Enabled"] = "true", ["Web:Writes:Repositories:0"] = "acme/other", ["Web:Writes:SingleInstance"] = "true" }
            : new Dictionary<string, string?>();
        var (host, writer, alice, token) = await StartAsync(Build(), settings);
        await using var _ = host;

        // The body too: the same JSON as a route that does not exist, so "off" cannot be told from
        // "absent", and the page reads it as a refusal — nothing was sent — not as "may have posted".
        var absent = await (await Post(host, "/no-such-route", new { opKey = Key() }, alice, Headers(token))).Body();
        Assert.Contains("\"error\":\"not-found\"", absent, StringComparison.Ordinal);
        foreach (var (path, method) in new[] { ("/comments", "POST"), ("/line-comments", "POST"), ("/threads/T_here1234/replies", "POST"), ("/threads/T_here1234/resolve", "POST"), ("/comments/C_1abcd", "PATCH"), ("/comments/C_1abcd/delete", "POST"), ("/markdown", "POST") })
        {
            var response = await Post(host, path, new { opKey = Key(), body = "hi" }, alice, Headers(token), method);
            response.AssertStatus(HttpStatusCode.NotFound);
            Assert.Equal(absent, await response.Body());
        }

        Assert.Empty(writer.Calls);
    }

    /// <summary>
    /// The limiter answers before any write runs: said in this server's JSON, which the page reads as
    /// "nothing was posted". An empty 429 read as "may or may not" and locked the box (found by
    /// review, 2026-10-20).
    /// </summary>
    [Fact]
    public async Task A_write_over_the_session_limit_is_refused_in_this_servers_own_words()
    {
        var settings = new Dictionary<string, string?>(WritesOn) { ["Web:Writes:PerSessionPerMinute"] = "1" };
        var (host, _, alice, token) = await StartAsync(Build(), settings);
        await using var _ = host;

        await Post(host, "/comments", new { opKey = Key(), body = "One." }, alice, Headers(token));
        var limited = await Post(host, "/comments", new { opKey = Key(), body = "Two." }, alice, Headers(token));

        limited.AssertStatus(HttpStatusCode.TooManyRequests);
        Assert.Contains("\"error\":\"rate-limited\"", await limited.Body(), StringComparison.Ordinal);
    }

    /// <summary>S4: each check alone refuses — the custom header, a JSON body, and the browser's own word on the origin.</summary>
    [Theory]
    [InlineData("no-header")]
    [InlineData("form")]
    [InlineData("cross-site")]
    [InlineData("foreign-origin")]
    public async Task A_request_that_could_be_forged_is_refused(string flaw)
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var headers = Headers(token);
        var type = "application/json";
        switch (flaw)
        {
            case "no-header":
                headers.Remove(AuthEndpoints.CsrfHeader);
                break;
            case "form":
                type = "application/x-www-form-urlencoded";
                break;
            case "cross-site":
                headers["Sec-Fetch-Site"] = "cross-site";
                break;
            case "foreign-origin":
                headers.Remove("Sec-Fetch-Site");
                headers["Origin"] = "https://evil.example";
                break;
        }

        var response = await host.SendAsync(HttpMethod.Post, "/api/repos/acme/plant/pulls/12/comments", alice, headers, JsonSerializer.Serialize(new { opKey = Key(), body = "hi" }), type);

        // A form post never even matches the write route: minimal APIs declare that they consume JSON,
        // endpoint selection passes it over, and the /api fallback answers 404 before any middleware
        // or handler of ours runs. Refused either way, and saying less.
        response.AssertStatus(flaw == "form" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden);
        Assert.Empty(writer.Writes);
    }

    /// <summary>S1: a review is in somebody's name. Signed out is refused before anything is sent.</summary>
    [Fact]
    public async Task Signed_out_nothing_is_written()
    {
        var (host, writer, _, token) = await StartAsync(Build());
        await using var __ = host;

        var response = await Post(host, "/comments", new { opKey = Key(), body = "hi" }, cookie: null, Headers(token));

        response.AssertStatus(HttpStatusCode.Unauthorized);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task A_body_past_the_cap_is_refused_before_it_is_read()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;

        var response = await Post(host, "/comments", new { opKey = Key(), body = new string('x', 70_000) }, alice, Headers(token));

        response.AssertStatus(HttpStatusCode.RequestEntityTooLarge);
        Assert.Empty(writer.Writes);
    }

    /// <summary>
    /// Every non-GET route is built by MapWrite (or is sign-out), so none can skip the shared rules.
    /// A new write route registered with MapPost turns this red.
    /// </summary>
    [Fact]
    public async Task Every_route_that_is_not_a_GET_goes_through_MapWrite()
    {
        var (host, _, _, _) = await StartAsync(Build());
        await using var __ = host;

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var writes = endpoints.Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m is not ("GET" or "HEAD")) == true).ToList();

        Assert.Contains(writes, e => e.RoutePattern.RawText!.EndsWith("/comments", StringComparison.Ordinal));
        Assert.All(writes.Where(e => e.RoutePattern.RawText != "/auth/logout"), e => Assert.NotNull(e.Metadata.GetMetadata<WriteEndpoint>()));
    }

    [Fact]
    public async Task Startup_refuses_writes_without_a_shared_ledger_or_on_a_repository_the_server_does_not_show()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestHost.StartAsync(Build().GitHub, new() { ["Web:Writes:Enabled"] = "true" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestHost.StartAsync(Build().GitHub, new()
        {
            ["Web:Repositories:0"] = "acme/plant",
            ["Web:Writes:Repositories:0"] = "acme/other",
        }));
    }

    // ---- a comment, and what happens when it may or may not have been posted --------------------

    [Fact]
    public async Task A_conversation_comment_is_posted_in_the_persons_name_carrying_its_operation_tag()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var key = Key();

        var response = await Post(host, "/comments", new { opKey = key, body = "Looks right." }, alice, Headers(token));

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("done", Json(await response.Body()).GetProperty("outcome").GetString());
        var write = Assert.Single(writer.Writes);
        Assert.Equal((AccessKind.User, "alice"), (write.Access.Kind, write.Access.Who));
        var posted = Assert.Single(writer.Comments.Values);
        Assert.StartsWith("Looks right.", posted.Body, StringComparison.Ordinal);
        Assert.True(ReviewTags.Carries(posted.Body, key));
    }

    /// <summary>A double click or a retry is one write: the second request returns the first one's outcome.</summary>
    [Fact]
    public async Task The_same_key_twice_is_one_comment_and_the_same_key_with_another_body_is_refused()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var key = Key();

        var first = Json(await (await Post(host, "/comments", new { opKey = key, body = "Once." }, alice, Headers(token))).Body());
        var again = await Post(host, "/comments", new { opKey = key, body = "Once." }, alice, Headers(token));
        var other = await Post(host, "/comments", new { opKey = key, body = "Something else." }, alice, Headers(token));

        again.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(first.GetProperty("id").GetString(), Json(await again.Body()).GetProperty("id").GetString());
        other.AssertStatus((HttpStatusCode)422);
        Assert.Contains("op-mismatch", await other.Body(), StringComparison.Ordinal);
        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task Two_identical_requests_at_once_make_one_call_to_GitHub()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var key = Key();
        writer.Hold = new TaskCompletionSource();

        var a = Post(host, "/comments", new { opKey = key, body = "Race." }, alice, Headers(token));
        var b = Post(host, "/comments", new { opKey = key, body = "Race." }, alice, Headers(token));
        for (var i = 0; i < 200 && writer.Writes.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(100);
        writer.Hold.SetResult();
        var answers = await Task.WhenAll(a, b);

        Assert.Single(writer.Writes);
        Assert.Contains(answers, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Contains(answers, r => r.StatusCode == HttpStatusCode.Accepted);
    }

    /// <summary>
    /// Two retries of a key whose first attempt GitHub refused, both reading "failed" before either
    /// acts — the window the ledger holds them in here, which real timing hits only rarely. Taking
    /// the key over is a compare-and-set, so one of them sends the comment and the other is told it
    /// is in progress: one comment, not two (found by review, 2026-10-20).
    /// </summary>
    [Fact]
    public async Task Two_retries_of_a_failed_attempt_take_the_key_over_once()
    {
        var ledger = new HoldingLedger();
        var (host, writer, alice, token) = await StartAsync(Build(), more: s => s.AddSingleton<IOperationLedger>(ledger));
        await using var _ = host;
        var key = Key();
        writer.Next.Enqueue(FakeWriter.Mode.Refused);
        (await Post(host, "/comments", new { opKey = key, body = "Again." }, alice, Headers(token))).AssertStatus(HttpStatusCode.UnprocessableEntity);

        ledger.HoldFailedReads = 2;
        var a = Post(host, "/comments", new { opKey = key, body = "Again." }, alice, Headers(token));
        var b = Post(host, "/comments", new { opKey = key, body = "Again." }, alice, Headers(token));
        var answers = await Task.WhenAll(a, b);

        Assert.Equal(2, writer.Writes.Count);
        Assert.Contains(answers, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Contains(answers, r => r.StatusCode == HttpStatusCode.Accepted);
    }

    /// <summary>
    /// The revision is part of the operation: the same key under another snapshot is refused as a
    /// different operation and nothing is sent. Found by review (2026-10-20): a retry of an approval
    /// that never reached the server, after a push and a reload, carried the new snapshot.
    /// </summary>
    [Fact]
    public async Task A_retry_under_another_snapshot_is_another_operation_and_nothing_is_sent()
    {
        var w = Build();
        var (host, writer, alice, first) = await StartAsync(w);
        await using var _ = host;
        var key = Key();
        writer.Next.Enqueue(FakeWriter.Mode.LostBefore);
        (await Post(host, "/comments", new { opKey = key, body = "Approve-ish." }, alice, Headers(first))).AssertStatus(HttpStatusCode.Accepted);

        w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { HeadSha = new string('9', 40) }, w.Repo.Pulls[0].Body);
        w.Repo.MergeBases[(w.MainTip, new string('9', 40))] = w.Start;
        var second = JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice)).Body()).RootElement.GetProperty("token").GetString()!;
        var retry = await Post(host, "/comments", new { opKey = key, body = "Approve-ish." }, alice, Headers(second));

        retry.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("op-mismatch", await retry.Body(), StringComparison.Ordinal);
        Assert.Single(writer.Writes);
    }

    /// <summary>
    /// Keys outlive snapshots (a week against eight hours). A retry under its own, now expired,
    /// snapshot learns how its operation ended — here, that GitHub did post it after all — and a
    /// fresh write under an expired snapshot is still refused: settling reads, it never starts.
    /// </summary>
    [Fact]
    public async Task A_retry_under_its_expired_snapshot_settles_its_own_operation_and_starts_nothing()
    {
        var clock = new Versio.Core.Abstractions.FakeClock(DateTimeOffset.UtcNow);
        var (host, writer, _, _) = await StartAsync(Build(), more: s => s.AddSingleton<Versio.Core.Abstractions.IClock>(clock));
        await using var __ = host;
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddDays(3), null, DateTimeOffset.UtcNow.AddMonths(6));
        var alice = $"{AccessResolver.SessionCookie}={session.Id}";
        var token = JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice)).Body()).RootElement.GetProperty("token").GetString()!;
        var key = Key();
        writer.Next.Enqueue(FakeWriter.Mode.LostBefore);
        (await Post(host, "/comments", new { opKey = key, body = "Late." }, alice, Headers(token))).AssertStatus(HttpStatusCode.Accepted);
        // It did reach GitHub after all, by the time anyone looks.
        writer.Comments["C_lateabcd"] = new FakeWriter.Comment("C_lateabcd", "IssueComment", "acme/plant", 12, "alice", ReviewTags.WithOperation("Late.", key), DateTimeOffset.UtcNow, null);

        clock.Advance(ReviewSnapshots.Lifetime + TimeSpan.FromMinutes(1));
        var settled = await Post(host, "/comments", new { opKey = key, body = "Late." }, alice, Headers(token));
        var fresh = await Post(host, "/comments", new { opKey = Key(), body = "New." }, alice, Headers(token));

        settled.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("C_lateabcd", Json(await settled.Body()).GetProperty("id").GetString());
        fresh.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("snapshot-expired", await fresh.Body(), StringComparison.Ordinal);
        Assert.Single(writer.Writes);
    }

    /// <summary>The in-memory ledger, holding readers of a failed entry until as many as asked have read it.</summary>
    private sealed class HoldingLedger : IOperationLedger
    {
        private readonly MemoryOperationLedger _inner = new();
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public int HoldFailedReads { get; set; }

        public async Task<Reservation> ReserveAsync(string key, string payloadHash, DateTimeOffset now, CancellationToken ct)
        {
            var found = await _inner.ReserveAsync(key, payloadHash, now, ct);
            if (HoldFailedReads > 0 && found.Existing is { State: OperationState.Failed })
            {
                if (Interlocked.Increment(ref _arrived) >= HoldFailedReads)
                {
                    _all.TrySetResult();
                }

                await _all.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }

            return found;
        }

        public Task RecordAsync(string key, OperationEntry entry, CancellationToken ct) => _inner.RecordAsync(key, entry, ct);

        public Task<bool> TryTakeOverAsync(string key, Reservation seen, OperationEntry next, CancellationToken ct) => _inner.TryTakeOverAsync(key, seen, next, ct);

        public Task<OperationEntry?> FindAsync(string key, CancellationToken ct) => _inner.FindAsync(key, ct);
    }

    /// <summary>
    /// GitHub posted the comment and the answer was lost. The server does not retry: it looks for
    /// its own tag, finds the comment, and reports it posted. One comment, not two.
    /// </summary>
    [Fact]
    public async Task A_comment_GitHub_posted_whose_answer_was_lost_is_found_not_posted_again()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        writer.Next.Enqueue(FakeWriter.Mode.LostAfter);

        var response = await Post(host, "/comments", new { opKey = Key(), body = "Lost answer." }, alice, Headers(token));

        response.AssertStatus(HttpStatusCode.OK);
        var body = Json(await response.Body());
        Assert.Equal("done", body.GetProperty("outcome").GetString());
        Assert.True(body.GetProperty("reconciled").GetBoolean());
        Assert.Single(writer.Comments);
        Assert.Single(writer.Writes);
    }

    /// <summary>
    /// The answer was lost and the comment is not there. Creating a comment is not safe to repeat,
    /// so it stays "may or may not have been posted" — on the retry too — and GitHub is never asked twice.
    /// </summary>
    [Fact]
    public async Task An_unknown_comment_that_cannot_be_found_stays_unknown_and_is_never_resent()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var key = Key();
        writer.Next.Enqueue(FakeWriter.Mode.LostBefore);

        var first = await Post(host, "/comments", new { opKey = key, body = "Maybe." }, alice, Headers(token));
        var retry = await Post(host, "/comments", new { opKey = key, body = "Maybe." }, alice, Headers(token));

        first.AssertStatus(HttpStatusCode.Accepted);
        Assert.Equal("unknown", Json(await first.Body()).GetProperty("outcome").GetString());
        retry.AssertStatus(HttpStatusCode.Accepted);
        Assert.Equal("unknown", Json(await retry.Body()).GetProperty("outcome").GetString());
        Assert.Single(writer.Writes);
        Assert.Empty(writer.Comments);
    }

    /// <summary>
    /// The process died after GitHub accepted the write and before the ledger recorded it: the entry
    /// is still pending, owned by nobody. A retry past the abandonment window reconciles first — the
    /// comment is found — and nothing is re-sent.
    /// </summary>
    [Fact]
    public async Task After_a_crash_mid_write_the_retry_reconciles_instead_of_posting_again()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        var key = Key(DateTimeOffset.UtcNow.AddMinutes(-3));
        var reserved = DateTimeOffset.UtcNow.AddMinutes(-2);
        var ledger = host.Services.GetRequiredService<IOperationLedger>();
        await ledger.RecordAsync($"user:alice|comment|acme/plant|12|{key}",
            // The hash as the server forms it: the payload, bound to the snapshot's head and base.
            new OperationEntry(ReviewTags.PayloadHash(ReviewTags.PayloadHash("Crashed."), w.Head, w.MainTip), OperationState.Pending, null, reserved, reserved), CancellationToken.None);
        writer.Comments["C_crashabcd"] = new FakeWriter.Comment("C_crashabcd", "IssueComment", "acme/plant", 12, "alice", ReviewTags.WithOperation("Crashed.", key), reserved, null);

        var retry = await Post(host, "/comments", new { opKey = key, body = "Crashed." }, alice, Headers(token));

        retry.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("C_crashabcd", Json(await retry.Body()).GetProperty("id").GetString());
        Assert.Empty(writer.Writes);
    }

    /// <summary>Expiry never turns a key into a fresh submission: an old key is refused, never re-run.</summary>
    [Fact]
    public async Task An_expired_or_malformed_key_is_refused_and_nothing_is_sent()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;

        var expired = await Post(host, "/comments", new { opKey = Key(DateTimeOffset.UtcNow - ReviewTags.KeyLifetime - TimeSpan.FromHours(1)), body = "Old." }, alice, Headers(token));
        var malformed = await Post(host, "/comments", new { opKey = "nope", body = "Bad." }, alice, Headers(token));

        expired.AssertStatus((HttpStatusCode)422);
        Assert.Contains("operation-expired", await expired.Body(), StringComparison.Ordinal);
        malformed.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task GitHubs_refusal_is_said_in_its_own_words_and_nothing_is_recorded_as_posted()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var key = Key();
        writer.Next.Enqueue(FakeWriter.Mode.Refused);

        var refused = await Post(host, "/comments", new { opKey = key, body = "No." }, alice, Headers(token));
        // Refused means nothing was written, so the same key may try again.
        var retried = await Post(host, "/comments", new { opKey = key, body = "No." }, alice, Headers(token));

        refused.AssertStatus((HttpStatusCode)422);
        Assert.Contains("Validation Failed", await refused.Body(), StringComparison.Ordinal);
        retried.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(2, writer.Writes.Count);
    }

    // ---- line comments --------------------------------------------------------------------------

    [Fact]
    public async Task A_line_comment_goes_on_the_snapshots_head_at_a_line_GitHubs_diff_shows()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;

        var response = await Post(host, "/line-comments", new { opKey = Key(), body = "Why TRUE?", path = "src/Pump.st", line = 3, side = "RIGHT" }, alice, Headers(token));

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal($"{w.Head} src/Pump.st RIGHT -3", Assert.Single(writer.Writes).Detail);
    }

    [Theory]
    [InlineData("src/Pump.st", 1, "RIGHT", null, "line-not-in-diff")]
    [InlineData("src/Two.st", 10, "RIGHT", 2, "line-not-in-diff")] // a range across two hunks
    [InlineData("src/Logo.bin", 1, "RIGHT", null, "inline-unavailable")]
    public async Task A_line_GitHub_would_refuse_is_refused_here_with_the_reason(string path, int line, string side, int? start, string error)
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;

        var response = await Post(host, "/line-comments", new { opKey = Key(), body = "x", path, line, side, startLine = start }, alice, Headers(token));

        response.AssertStatus((HttpStatusCode)422);
        Assert.Contains(error, await response.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task A_file_without_line_diff_still_takes_a_comment_on_the_whole_file()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;

        var response = await Post(host, "/line-comments", new { opKey = Key(), body = "Is this logo meant to change?", path = "src/Logo.bin" }, alice, Headers(token));

        response.AssertStatus(HttpStatusCode.OK);
        Assert.Contains(" src/Logo.bin RIGHT -", Assert.Single(writer.Writes).Detail, StringComparison.Ordinal);
    }

    /// <summary>A line is a position in one revision: when the pull request moved, the reviewer reloads first.</summary>
    [Fact]
    public async Task A_line_comment_on_a_snapshot_the_pull_request_has_moved_past_is_refused()
    {
        var w = Build();
        var (host, writer, alice, token) = await StartAsync(w);
        await using var _ = host;
        w.Repo.Pulls[0] = (w.Repo.Pulls[0].Info with { HeadSha = new string('9', 40) }, "Why");

        var response = await Post(host, "/line-comments", new { opKey = Key(), body = "x", path = "src/Pump.st", line = 3, side = "RIGHT" }, alice, Headers(token));

        response.AssertStatus(HttpStatusCode.Conflict);
        Assert.Contains("snapshot-stale", await response.Body(), StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    // ---- threads ----------------------------------------------------------------------------------

    /// <summary>
    /// S7: node ids are global. A thread of another repository, named under this pull request's URL,
    /// is "not found" — the URL's scope check said nothing about it.
    /// </summary>
    [Fact]
    public async Task A_thread_from_another_repository_cannot_be_replied_to_or_resolved_through_this_one()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;

        var reply = await Post(host, "/threads/T_elsewhere/replies", new { opKey = Key(), body = "hi" }, alice, Headers(token));
        var resolve = await Post(host, "/threads/T_elsewhere/resolve", new { opKey = Key(), resolved = true }, alice, Headers(token));

        reply.AssertStatus(HttpStatusCode.NotFound);
        resolve.AssertStatus(HttpStatusCode.NotFound);
        Assert.Empty(writer.Writes);
        Assert.False(writer.Threads["T_elsewhere"].Resolved);
    }

    [Fact]
    public async Task A_reply_goes_to_its_thread_carrying_its_tag()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var key = Key();

        var response = await Post(host, "/threads/T_here1234/replies", new { opKey = key, body = "Plant data says 0.2." }, alice, Headers(token));

        response.AssertStatus(HttpStatusCode.OK);
        var reply = Assert.Single(writer.Comments.Values);
        Assert.Equal("T_here1234", reply.ThreadId);
        Assert.True(ReviewTags.Carries(reply.Body, key));
    }

    /// <summary>
    /// Resolving is safe to repeat. An uncertain first attempt is reconciled by reading the thread:
    /// still unresolved, it is simply done again on the retry.
    /// </summary>
    [Fact]
    public async Task An_uncertain_resolve_is_read_back_and_done_again_because_it_is_safe_to_repeat()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        var key = Key();
        writer.Next.Enqueue(FakeWriter.Mode.LostBefore);

        var first = await Post(host, "/threads/T_here1234/resolve", new { opKey = key, resolved = true }, alice, Headers(token));
        var retry = await Post(host, "/threads/T_here1234/resolve", new { opKey = key, resolved = true }, alice, Headers(token));

        first.AssertStatus(HttpStatusCode.Accepted);
        retry.AssertStatus(HttpStatusCode.OK);
        Assert.True(writer.Threads["T_here1234"].Resolved);
        Assert.Equal(2, writer.Writes.Count);
    }

    [Fact]
    public async Task An_edit_and_a_delete_reach_only_comments_of_this_pull_request()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;
        writer.Comments["C_minexabc"] = new FakeWriter.Comment("C_minexabc", "IssueComment", "acme/plant", 12, "alice", "first", DateTimeOffset.UtcNow, null);
        writer.Comments["C_otherabc"] = new FakeWriter.Comment("C_otherabc", "IssueComment", "acme/plant", 99, "alice", "elsewhere", DateTimeOffset.UtcNow, null);
        var key = Key();

        var edited = await Post(host, "/comments/C_minexabc", new { opKey = key, body = "second" }, alice, Headers(token), "PATCH");
        var foreign = await Post(host, "/comments/C_otherabc/delete", new { opKey = Key() }, alice, Headers(token));
        var deleted = await Post(host, "/comments/C_minexabc/delete", new { opKey = Key() }, alice, Headers(token));

        edited.AssertStatus(HttpStatusCode.OK);
        foreign.AssertStatus(HttpStatusCode.NotFound);
        deleted.AssertStatus(HttpStatusCode.OK);
        Assert.False(writer.Comments.ContainsKey("C_minexabc"));
        Assert.True(writer.Comments.ContainsKey("C_otherabc"));
    }

    [Fact]
    public async Task The_preview_renders_with_Cores_Markdown_and_writes_nothing()
    {
        var (host, writer, alice, token) = await StartAsync(Build());
        await using var _ = host;

        var response = await Post(host, "/markdown", new { body = "**bold** <script>x</script>" }, alice, Headers(token));

        var html = Json(await response.Body()).GetProperty("html").GetString()!;
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(writer.Calls);
    }

    /// <summary>The snapshot says whether to draw write controls: on for a signed-in person on a writable repository only.</summary>
    [Fact]
    public async Task The_snapshot_says_writable_only_for_a_signed_in_person_on_an_opted_in_repository()
    {
        var w = Build();
        var (host, _, alice, _) = await StartAsync(w);
        await using var _ = host;

        var signedIn = Json(await (await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot", alice)).Body());
        var anonymous = Json(await (await host.GetAsync("/api/repos/acme/plant/pulls/12/snapshot")).Body());

        Assert.True(signedIn.GetProperty("writable").GetBoolean());
        Assert.False(anonymous.GetProperty("writable").GetBoolean());
    }
}
