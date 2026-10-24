using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The real writer against GitHub-shaped answers: the three outcomes (done, refused, unknown) are
/// told apart correctly, because telling a lost success from a failure is what keeps a retry from
/// posting twice (C5).
/// </summary>
public sealed class GitHubWriterTests : IAsyncLifetime
{
    private WebApplication? _api;
    private Uri? _address;
    private readonly ConcurrentQueue<(string Method, string Path, string Auth, string Body)> _requests = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            http.Request.EnableBuffering();
            var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
            http.Request.Body.Position = 0;
            _requests.Enqueue((http.Request.Method, http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization.ToString(), body));
            await next(http);
        });

        app.MapPost("/repos/acme/plant/issues/12/comments", () => Results.Json(new { id = 1, node_id = "IC_1" }, statusCode: 201));
        app.MapPost("/repos/acme/plant/issues/13/comments", () => Results.StatusCode(502));
        app.MapPost("/repos/acme/plant/issues/14/comments", () => Results.Json(new { message = "Validation Failed" }, statusCode: 422));
        app.MapPost("/repos/acme/plant/issues/15/comments", async () =>
        {
            await Task.Delay(3000);
            return Results.Json(new { node_id = "IC_late" }, statusCode: 201);
        });
        app.MapPost("/repos/acme/plant/pulls/12/comments", () => Results.Json(new { node_id = "PRRC_1" }, statusCode: 201));
        app.MapPost("/repos/acme/plant/pulls/12/reviews", () => Results.Json(new { node_id = "PRR_1" }, statusCode: 200));
        app.MapPut("/repos/acme/plant/pulls/12/merge", () => Results.Json(new { sha = new string('m', 40), merged = true }));
        app.MapPut("/repos/acme/plant/pulls/13/merge", () => Results.Json(new { message = "Head branch was modified. Review and try the merge again." }, statusCode: 409));
        app.MapPut("/repos/acme/plant/pulls/12/update-branch", () => Results.Json(new { message = "Updating pull request branch." }, statusCode: 202));
        // Submitted reviews, a page of others' before the viewer's own tagged one; and a list with no end.
        app.MapGet("/repos/acme/plant/pulls/20/reviews", (HttpContext http) =>
        {
            var page = int.Parse(http.Request.Query["page"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            return page == 1
                ? Results.Json(Enumerable.Range(0, 100).Select(i => new { node_id = $"PRR_other{i}", user = new { login = "jo" }, body = "<!-- versio:op v1 K -->" }).ToArray())
                : Results.Json(new object[] { new { node_id = "PRR_mine", user = new { login = "alice" }, body = "ok\n\n<!-- versio:op v1 K -->" } });
        });
        app.MapGet("/repos/acme/plant/pulls/21/reviews", () =>
            Results.Json(Enumerable.Range(0, 100).Select(i => new { node_id = $"PRR_x{i}", user = new { login = "jo" }, body = "" }).ToArray()));
        app.MapPost("/repos/acme/plant/pulls", () => Results.Json(new { number = 13, node_id = "PR_13" }, statusCode: 201));
        app.MapPatch("/repos/acme/plant/pulls/12", () => Results.Json(new { number = 12 }));
        app.MapPatch("/repos/acme/plant/issues/12", () => Results.Json(new { number = 12 }));
        // A deleted ref answers 204 with no body; one already gone, 422 in GitHub's words; a created ref, 201.
        app.MapDelete("/repos/acme/plant/git/refs/heads/fix", () => Results.StatusCode(204));
        app.MapDelete("/repos/acme/plant/git/refs/heads/gone", () => Results.Json(new { message = "Reference does not exist" }, statusCode: 422));
        app.MapPost("/repos/acme/plant/git/refs", () => Results.Json(new { @ref = "refs/heads/fix" }, statusCode: 201));
        app.MapPost("/repos/acme/plant/pulls/12/requested_reviewers", () => Results.Json(new { number = 12 }, statusCode: 201));
        app.MapDelete("/repos/acme/plant/issues/12/labels/gone", () => Results.Json(new { message = "Label does not exist" }, statusCode: 404));
        app.MapDelete("/repos/acme/plant/issues/12/labels/{name}", () => Results.Json(Array.Empty<object>()));
        app.MapGet("/repos/acme/plant/pulls", () => Results.Json(new object[]
        {
            new { number = 20, user = new { login = "mallory" }, body = "copied <!-- versio:op v1 K -->" },
            new { number = 21, user = new { login = "alice" }, body = (string?)null },
            new { number = 22, user = new { login = "alice" }, body = "mine\n\n<!-- versio:op v1 K -->" },
        }));
        app.MapPost("/graphql", async (HttpContext http) =>
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
            var query = doc.RootElement.GetProperty("query").GetString()!;
            var id = doc.RootElement.GetProperty("variables").TryGetProperty("id", out var v) ? v.GetString() : null;
            // Execution errors as GitHub sends them: the mutation's own data beside an internal error;
            // an untyped error with the field null; and a request error, with no data at all.
            if (id == "partial")
            {
                return Results.Json(new { data = new { resolveReviewThread = new { thread = new { isResolved = true } } }, errors = new[] { new { type = "INTERNAL", message = "Something went wrong" } } });
            }

            // The mutation's data, and a typed error on some field beside it: still it ran.
            if (id == "partial-typed")
            {
                return Results.Json(new { data = new { resolveReviewThread = new { thread = new { isResolved = true } } }, errors = new[] { new { type = "FORBIDDEN", message = "Resource not accessible by integration" } } });
            }

            if (id == "untyped")
            {
                return Results.Json(new { data = new { resolveReviewThread = (object?)null }, errors = new[] { new { message = "Something went wrong while executing your query." } } });
            }

            if (id == "invalid")
            {
                return Results.Json(new { errors = new[] { new { message = "Parse error on \"}\"" } } });
            }

            if (id == "refused")
            {
                return Results.Json(new { data = (object?)null, errors = new[] { new { type = "FORBIDDEN", message = "Resource not accessible by integration" } } });
            }

            if (query.Contains("addPullRequestReview(", StringComparison.Ordinal))
            {
                return Results.Json(new { data = new { addPullRequestReview = new { pullRequestReview = new { id = "PRR_pending" } } } });
            }

            if (query.Contains("createCommitOnBranch(", StringComparison.Ordinal))
            {
                return Results.Json(new { data = new { createCommitOnBranch = new { commit = new { oid = new string('s', 40) } } } });
            }

            if (query.Contains("on Reactable", StringComparison.Ordinal))
            {
                return Results.Json(new { data = new { node = new { reactionGroups = new object[] { new { content = "HEART", viewerHasReacted = true }, new { content = "EYES", viewerHasReacted = false } } } } });
            }

            return query.Contains("resolveReviewThread", StringComparison.Ordinal)
                ? Results.Json(new { data = new { resolveReviewThread = new { thread = new { isResolved = true } } } })
                : Results.Json(new { data = new { node = (object?)null } });
        });
        app.MapGet("/repos/acme/plant/issues/12/comments", () => Results.Json(new object[]
        {
            new { node_id = "IC_other", user = new { login = "mallory" }, body = "copied <!-- versio:op v1 K --> tag" },
        }));
        app.MapGet("/repos/acme/plant/pulls/12/comments", () => Results.Json(new object[]
        {
            new { node_id = "PRRC_mine", user = new { login = "alice" }, body = "mine\n\n<!-- versio:op v1 K -->" },
        }));

        await app.StartAsync();
        _api = app;
        _address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.StopAsync();
            await _api.DisposeAsync();
        }
    }

    private GitHubWriter Writer()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient, c => c.Timeout = TimeSpan.FromMilliseconds(800));
        var options = Options.Create(new WebOptions { GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/') } });
        return new GitHubWriter(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);
    }

    private static readonly RepoAccess Alice = new(AccessKind.User, "alice-token", "alice");

    [Fact]
    public async Task A_comment_is_posted_with_the_persons_token_and_GitHubs_id_comes_back()
    {
        var done = await Writer().AddConversationCommentAsync(Alice, "acme", "plant", 12, "hello", CancellationToken.None);

        Assert.Equal((WriteOutcome.Done, "IC_1"), (done.Outcome, done.Value));
        var request = Assert.Single(_requests);
        Assert.Equal("Bearer alice-token", request.Auth);
        Assert.Contains("\"body\":\"hello\"", request.Body, StringComparison.Ordinal);
    }

    /// <summary>A 5xx or a timeout after the request left is not a failure: GitHub may have done it.</summary>
    [Fact]
    public async Task A_502_or_a_timeout_is_unknown_never_refused()
    {
        var bad = await Writer().AddConversationCommentAsync(Alice, "acme", "plant", 13, "x", CancellationToken.None);
        var slow = await Writer().AddConversationCommentAsync(Alice, "acme", "plant", 15, "x", CancellationToken.None);

        Assert.Equal(WriteOutcome.Unknown, bad.Outcome);
        Assert.Equal(WriteOutcome.Unknown, slow.Outcome);
    }

    [Fact]
    public async Task A_4xx_or_a_GraphQL_error_is_refused_with_GitHubs_own_words()
    {
        var rest = await Writer().AddConversationCommentAsync(Alice, "acme", "plant", 14, "x", CancellationToken.None);
        var graph = await Writer().SetThreadResolvedAsync(Alice, "refused", true, CancellationToken.None);
        var resolved = await Writer().SetThreadResolvedAsync(Alice, "T_1", true, CancellationToken.None);

        Assert.Equal((WriteOutcome.Refused, "Validation Failed"), (rest.Outcome, rest.Message));
        Assert.Equal((WriteOutcome.Refused, "Resource not accessible by integration"), (graph.Outcome, graph.Message));
        Assert.Equal(WriteOutcome.Done, resolved.Outcome);
    }

    /// <summary>S1, the second wall: the writer itself refuses anything but a person's token, before any call.</summary>
    [Fact]
    public async Task An_installation_or_anonymous_credential_is_refused_before_anything_is_sent()
    {
        var installation = await Writer().AddConversationCommentAsync(new RepoAccess(AccessKind.Installation, "installation-token", "app"), "acme", "plant", 12, "x", CancellationToken.None);
        var anonymous = await Writer().ReplyAsync(RepoAccess.Anonymous, "T_1", "x", CancellationToken.None);

        Assert.Equal(WriteOutcome.Refused, installation.Outcome);
        Assert.Equal(WriteOutcome.Refused, anonymous.Outcome);
        Assert.Empty(_requests);
    }

    /// <summary>Reconciliation finds the viewer's own tagged comment — not someone who copied the tag.</summary>
    [Fact]
    public async Task Reconciliation_finds_only_the_viewers_own_tagged_comment()
    {
        var found = await Writer().FindTaggedCommentAsync(Alice, "acme", "plant", 12, "<!-- versio:op v1 K -->", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("PRRC_mine", found.Value);
        Assert.Contains(_requests, r => r.Path.StartsWith("/repos/acme/plant/issues/12/comments?since=", StringComparison.Ordinal));
    }

    /// <summary>
    /// A review is bound to the commit it is CREATED on (docs/web/PLAN.md, C2): the one-shot form
    /// carries commit_id, and a pending review is started with commitOID — submitting takes none.
    /// </summary>
    [Fact]
    public async Task A_review_names_its_commit_when_it_is_created_both_ways()
    {
        var commit = new string('d', 40);

        var oneShot = await Writer().SubmitReviewAsync(Alice, "acme", "plant", 12, commit, "APPROVE", "", CancellationToken.None);
        var pending = await Writer().StartPendingReviewAsync(Alice, "PR_12", commit, CancellationToken.None);

        Assert.Equal(("PRR_1", "PRR_pending"), (oneShot.Value, pending.Value));
        var review = _requests.Single(r => r.Path == "/repos/acme/plant/pulls/12/reviews").Body;
        Assert.Contains($"\"commit_id\":\"{commit}\"", review, StringComparison.Ordinal);
        Assert.Contains("\"event\":\"APPROVE\"", review, StringComparison.Ordinal);
        Assert.Contains($"\"commit\":\"{commit}\"", _requests.Single(r => r.Body.Contains("addPullRequestReview(", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_comment_names_its_commit_line_and_side_and_a_range_its_start()
    {
        await Writer().AddLineCommentAsync(Alice, "acme", "plant", 12, new LineComment(new string('d', 40), "src/Pump.st", 7, "RIGHT", 5, "x"), CancellationToken.None);
        await Writer().AddLineCommentAsync(Alice, "acme", "plant", 12, new LineComment(new string('d', 40), "src/Logo.bin", null, "RIGHT", null, "x"), CancellationToken.None);

        var bodies = _requests.Where(r => r.Path == "/repos/acme/plant/pulls/12/comments").Select(r => r.Body).ToList();
        Assert.Contains("\"line\":7", bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"start_line\":5", bodies[0], StringComparison.Ordinal);
        Assert.Contains($"\"commit_id\":\"{new string('d', 40)}\"", bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"subject_type\":\"file\"", bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("\"line\"", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_request_is_opened_from_a_branch_of_this_repository_and_its_number_comes_back()
    {
        var created = await Writer().CreatePullAsync(Alice, "acme", "plant", new NewPull("main", "feature", "Add the valve", "Adds it.", true), CancellationToken.None);

        Assert.Equal((WriteOutcome.Done, 13), (created.Outcome, created.Value));
        var body = _requests.Single(r => r.Method == "POST" && r.Path == "/repos/acme/plant/pulls").Body;
        Assert.Contains("\"head\":\"feature\"", body, StringComparison.Ordinal);
        Assert.Contains("\"base\":\"main\"", body, StringComparison.Ordinal);
        Assert.Contains("\"draft\":true", body, StringComparison.Ordinal);
    }

    /// <summary>A field not being changed is not sent: GitHub keeps whatever it is now, whoever changed it.</summary>
    [Fact]
    public async Task An_edit_sends_only_the_fields_it_changes()
    {
        await Writer().EditPullAsync(Alice, "acme", "plant", 12, new PullEdit("New title", null, null), CancellationToken.None);

        var body = _requests.Single(r => r.Method == "PATCH").Body;
        Assert.Equal("{\"title\":\"New title\"}", body);
    }

    [Fact]
    public async Task Removing_a_label_already_gone_is_what_was_asked_and_counts_as_done()
    {
        var done = await Writer().SetLabelsAsync(Alice, "acme", "plant", 12, [], ["gone", "plc"], CancellationToken.None);

        Assert.Equal(WriteOutcome.Done, done.Outcome);
        Assert.Equal(2, _requests.Count(r => r.Method == "DELETE"));
    }

    /// <summary>Reconciling an opening finds the viewer's own pull request carrying the tag — not one someone copied it into.</summary>
    [Fact]
    public async Task Reconciling_an_opening_finds_only_the_viewers_own_tagged_pull_request()
    {
        var found = await Writer().FindCreatedPullAsync(Alice, "acme", "plant", "feature", "main", "<!-- versio:op v1 K -->", CancellationToken.None);

        Assert.Equal(22, found.Value);
        var query = _requests.Single(r => r.Method == "GET").Path;
        Assert.Contains("head=acme%3Afeature", query, StringComparison.Ordinal);
        Assert.Contains("state=all", query, StringComparison.Ordinal);
    }

    /// <summary>The merge carries the reviewed head as GitHub's precondition, and GitHub's 409 for a moved head is a refusal, never "unknown".</summary>
    [Fact]
    public async Task A_merge_names_its_head_and_a_moved_head_is_refused_in_GitHubs_words()
    {
        var head = new string('d', 40);
        var merged = await Writer().MergePullAsync(Alice, "acme", "plant", 12, head, "SQUASH", null, null, CancellationToken.None);
        var moved = await Writer().MergePullAsync(Alice, "acme", "plant", 13, head, "MERGE", null, null, CancellationToken.None);

        Assert.Equal((WriteOutcome.Done, new string('m', 40)), (merged.Outcome, merged.Value));
        var body = _requests.First(r => r.Method == "PUT" && r.Path.EndsWith("/12/merge", StringComparison.Ordinal)).Body;
        Assert.Contains($"\"sha\":\"{head}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"merge_method\":\"squash\"", body, StringComparison.Ordinal);
        Assert.Equal(WriteOutcome.Refused, moved.Outcome);
        Assert.Contains("Head branch was modified", moved.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updating_the_branch_and_auto_merge_name_the_reviewed_head()
    {
        var head = new string('d', 40);
        var updated = await Writer().UpdateBranchAsync(Alice, "acme", "plant", 12, head, "MERGE", "PR_12", CancellationToken.None);
        var auto = await Writer().EnableAutoMergeAsync(Alice, "PR_12", "MERGE", head, CancellationToken.None);

        var rebased = await Writer().UpdateBranchAsync(Alice, "acme", "plant", 12, head, "REBASE", "PR_12", CancellationToken.None);
        var deleted = await Writer().DeleteBranchAsync(Alice, "acme", "plant", "fix", CancellationToken.None);
        var gone = await Writer().DeleteBranchAsync(Alice, "acme", "plant", "gone", CancellationToken.None);
        var restored = await Writer().CreateBranchAsync(Alice, "acme", "plant", "fix", head, CancellationToken.None);

        Assert.Equal((WriteOutcome.Done, WriteOutcome.Done), (updated.Outcome, auto.Outcome));
        // A 204 has no body and is done; a ref already gone is what was asked; a rebase goes by GraphQL with the head named.
        Assert.Equal((WriteOutcome.Done, WriteOutcome.Done, WriteOutcome.Done, WriteOutcome.Done), (rebased.Outcome, deleted.Outcome, gone.Outcome, restored.Outcome));
        Assert.Contains($"\"head\":\"{head}\"", _requests.Single(r => r.Body.Contains("updatePullRequestBranch", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Contains("\"ref\":\"refs/heads/fix\"", _requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/git/refs", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Contains($"\"expected_head_sha\":\"{head}\"", _requests.Single(r => r.Path.EndsWith("/update-branch", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        var mutation = _requests.Single(r => r.Body.Contains("enablePullRequestAutoMerge", StringComparison.Ordinal)).Body;
        Assert.Contains("expectedHeadOid", mutation, StringComparison.Ordinal);
        Assert.Contains($"\"head\":\"{head}\"", mutation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A GraphQL error proves nothing ran only before execution: no data at all, or every error of a
    /// type GitHub raises before acting with nothing returned. Found by review (2026-10-20): data
    /// returned beside an internal error was read as refused, which released the operation key.
    /// </summary>
    [Fact]
    public async Task A_GraphQL_error_is_a_refusal_only_when_it_proves_nothing_ran()
    {
        var partial = await Writer().SetThreadResolvedAsync(Alice, "partial", true, CancellationToken.None);
        var untyped = await Writer().SetThreadResolvedAsync(Alice, "untyped", true, CancellationToken.None);
        var invalid = await Writer().SetThreadResolvedAsync(Alice, "invalid", true, CancellationToken.None);
        var refused = await Writer().SetThreadResolvedAsync(Alice, "refused", true, CancellationToken.None);
        var partialTyped = await Writer().SetThreadResolvedAsync(Alice, "partial-typed", true, CancellationToken.None);

        Assert.Equal(WriteOutcome.Unknown, partial.Outcome);
        Assert.Equal(WriteOutcome.Unknown, partialTyped.Outcome);
        Assert.Equal(WriteOutcome.Unknown, untyped.Outcome);
        Assert.Equal(WriteOutcome.Refused, invalid.Outcome);
        Assert.Equal(WriteOutcome.Refused, refused.Outcome);
    }

    /// <summary>
    /// Step 5's writes, in GitHub's shapes: teams ride beside people in the reviewers call; a commit
    /// carries the expected head and base64 contents; a reaction, a lock and a milestone name what
    /// they change; the viewer's reaction is read back from the subject's groups.
    /// </summary>
    [Fact]
    public async Task Reviewers_commits_reactions_locks_and_milestones_are_sent_in_GitHubs_shapes()
    {
        var head = new string('d', 40);
        var reviewers = await Writer().SetReviewersAsync(Alice, "acme", "plant", 12, new ReviewerChange(["sam"], [], ["plc-controls"], []), CancellationToken.None);
        var commit = await Writer().CommitFilesAsync(Alice, "acme", "plant", "fix", head, "Apply suggestion from code review\n\n<!-- versio:op v1 K -->", [new FileChange("src/Pump.st", "x\n"u8.ToArray())], CancellationToken.None);
        var reaction = await Writer().SetReactionAsync(Alice, "IC_1", "HEART", true, CancellationToken.None);
        var locked = await Writer().SetLockedAsync(Alice, "PR_12", true, "RESOLVED", CancellationToken.None);
        var milestone = await Writer().SetMilestoneAsync(Alice, "acme", "plant", 12, null, CancellationToken.None);
        var reacted = await Writer().ViewerReactedAsync(Alice, "IC_1", "HEART", CancellationToken.None);
        var notReacted = await Writer().ViewerReactedAsync(Alice, "IC_1", "EYES", CancellationToken.None);

        Assert.Equal((WriteOutcome.Done, WriteOutcome.Done, WriteOutcome.Done, WriteOutcome.Done, WriteOutcome.Done), (reviewers.Outcome, commit.Outcome, reaction.Outcome, locked.Outcome, milestone.Outcome));
        Assert.Equal(new string('s', 40), commit.Value);
        Assert.Contains("\"team_reviewers\":[\"plc-controls\"]", _requests.Single(r => r.Path.EndsWith("/requested_reviewers", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        var made = _requests.Single(r => r.Body.Contains("createCommitOnBranch", StringComparison.Ordinal)).Body;
        Assert.Contains($"\"head\":\"{head}\"", made, StringComparison.Ordinal);
        Assert.Contains("\"headline\":\"Apply suggestion from code review\"", made, StringComparison.Ordinal);
        Assert.Contains($"\"contents\":\"{Convert.ToBase64String("x\n"u8.ToArray())}\"", made, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"HEART\"", _requests.Single(r => r.Body.Contains("addReaction", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"RESOLVED\"", _requests.Single(r => r.Body.Contains("lockLockable", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        // The field is sent even when null: that is how a milestone is cleared.
        Assert.Equal("{\"milestone\":null}", _requests.Single(r => r.Path.EndsWith("/issues/12", StringComparison.Ordinal)).Body);
        Assert.Equal((true, false), (reacted.Value, notReacted.Value));
    }

    /// <summary>
    /// "Not found" only from a whole list. Found in review (2026-10-20): one page was searched, so
    /// a tag on the 101st review was "not there" — the wrong answer for reconciliation. Past the
    /// bound the search cannot tell, and says so, so the operation stays unknown.
    /// </summary>
    [Fact]
    public async Task Reconciliation_searches_every_page_and_past_the_bound_cannot_tell()
    {
        var found = await Writer().FindTaggedReviewAsync(Alice, "acme", "plant", 20, "<!-- versio:op v1 K -->", CancellationToken.None);
        var endless = await Writer().FindTaggedReviewAsync(Alice, "acme", "plant", 21, "<!-- versio:op v1 K -->", CancellationToken.None);

        Assert.Equal("PRR_mine", found.Value);
        Assert.False(endless.Ok);
        Assert.Equal(GitHubWriter.SearchPages, _requests.Count(r => r.Path.StartsWith("/repos/acme/plant/pulls/21/reviews", StringComparison.Ordinal)));
    }
}
