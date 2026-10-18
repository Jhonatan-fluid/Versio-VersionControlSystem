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
        app.MapPost("/graphql", async (HttpContext http) =>
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
            var query = doc.RootElement.GetProperty("query").GetString()!;
            var id = doc.RootElement.GetProperty("variables").TryGetProperty("id", out var v) ? v.GetString() : null;
            if (id == "refused")
            {
                return Results.Json(new { data = (object?)null, errors = new[] { new { type = "FORBIDDEN", message = "Resource not accessible by integration" } } });
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
}
