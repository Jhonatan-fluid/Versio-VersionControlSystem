using System.Collections.Concurrent;
using System.Net;
using System.Text;
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
/// The real GitHub client against a local server answering in GitHub's shapes: trees, raw blobs,
/// LFS pointers, and the refusals the page must word correctly.
/// </summary>
public sealed class GitHubRepoSourceTests : IAsyncLifetime
{
    private const string Oid = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>A real LFS object: its content, and the pointer's oid for it.</summary>
    private static readonly byte[] LfsContent = Encoding.UTF8.GetBytes("<Document>the real file</Document>\n");

    private static readonly string RealOid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(LfsContent)).ToLowerInvariant();

    /// <summary>The LFS download host, answered in memory: the guard requires https on 443.</summary>
    private sealed class LfsStore(byte[] answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(answer) });
    }

    private WebApplication? _api;
    private Uri? _address;
    private readonly ConcurrentQueue<(string Path, string? Auth)> _requests = new();
    private string _lfsHref = "https://169.254.169.254/latest/meta-data/";
    private readonly ConcurrentQueue<string> _appJwts = new();
    private readonly ConcurrentQueue<string> _mintBodies = new();
    private readonly ConcurrentQueue<string> _graphQueries = new();
    private int _notModified;
    private readonly TaskCompletionSource _releaseHeld = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            _requests.Enqueue((http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization.ToString()));
            await next(http);
        });

        app.MapGet("/repos/acme/plant", () => Results.Json(new { name = "plant", @private = false, default_branch = "main", owner = new { login = "acme" } }));
        app.MapGet("/repos/acme/plant/commits/main", (HttpContext http) =>
            // GitHub's SHA-only answer: forty characters, no JSON, no patches.
            http.Request.Headers.Accept.ToString() == "application/vnd.github.sha"
                ? Results.Text(new string('c', 40), "application/vnd.github.sha")
                : Results.StatusCode(415));
        app.MapGet("/repos/acme/plant/branches", (int per_page, int page) =>
            // 250 branches, in GitHub's pages.
            Results.Json(Enumerable.Range(0, 250).Skip((page - 1) * per_page).Take(per_page)
                .Select(i => new { name = $"b{i}", commit = new { sha = new string('f', 40) } })));
        app.MapGet("/repos/acme/endless/branches", (int per_page, int page) =>
            Results.Json(Enumerable.Range(0, per_page).Select(i => new { name = $"p{page}-{i}", commit = new { sha = new string('f', 40) } })));
        app.MapGet("/repos/acme/broken", () => Results.Json(new { unexpected = true }));

        // Pulls and releases in GitHub's own shapes, the odd corners included: a JSON-null body,
        // a draft release, a release with no name, a draft pull request. Binding `state` makes a
        // request without it a 400, and answering closed states empty proves state=open is sent.
        app.MapGet("/repos/acme/plant/pulls", (string state) => state != "open" ? Results.Json(Array.Empty<object>()) : Results.Json(new object[]
        {
            new
            {
                number = 7, title = "Fix interlock", user = new { login = "jo" }, updated_at = "2026-10-15T10:00:00Z",
                @base = new { @ref = "main" }, head = new { @ref = "fix-interlock", sha = new string('d', 40) }, draft = true,
            },
        }));
        app.MapGet("/repos/acme/plant/pulls/7", () => Results.Json(new
        {
            number = 7, title = "Fix interlock", user = new { login = "jo" }, updated_at = "2026-10-15T10:00:00Z",
            @base = new { @ref = "main" }, head = new { @ref = "fix-interlock", sha = new string('d', 40) }, draft = true,
            body = (string?)null,
        }));
        // A pull request's files in GitHub's pages: 7 has 150 (a full page, then a short one), 8 has
        // a full page on every page, as a pull request past GitHub's 3,000 cap answers.
        app.MapGet("/repos/acme/plant/pulls/{number:int}/files", (int number, int per_page, int page) =>
        {
            var total = number == 8 ? int.MaxValue : 150;
            var start = (page - 1) * per_page;
            return Results.Json(Enumerable.Range(start, Math.Max(0, Math.Min(per_page, total - start))).Select(i => i == 0
                ? (object)new { filename = "src/New.st", status = "renamed", previous_filename = "src/Old.st", patch = (string?)null }
                : new { filename = $"f{i}.st", status = "modified", patch = "@@ -1 +1 @@\n-a\n+b" }));
        });
        // The compare answer. A head of 'e's answers a merge-base that is not a commit id, which
        // must be refused rather than compared against; a base starting "full" lists GitHub's cap.
        app.MapGet("/repos/acme/plant/compare/{range}", (string range) => Results.Json(new
        {
            merge_base_commit = new { sha = range.EndsWith(new string('e', 40), StringComparison.Ordinal) ? "not-a-sha" : new string('B', 40) },
            files = Enumerable.Range(0, range.StartsWith("full", StringComparison.Ordinal) ? 300 : 2)
                .Select(i => new { filename = $"c{i}.st", status = "added", patch = "@@ -0,0 +1 @@\n+x" }),
        }));
        // GraphQL in GitHub's shapes. Threads come in two pages, and the first thread's comments
        // overflow into a node query, so both levels of paging are exercised. Pull request 404
        // answers as GitHub does: 200 with a NOT_FOUND error.
        app.MapPost("/graphql", async (HttpContext http) =>
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
            var query = doc.RootElement.GetProperty("query").GetString()!;
            var variables = doc.RootElement.GetProperty("variables");
            _graphQueries.Enqueue(query);
            object Comment(string id) => new { id, author = new { login = "jo" }, body = "c " + id, createdAt = "2026-10-17T09:00:00Z", commit = new { oid = new string('d', 40) }, originalCommit = (object?)null };
            if (variables.TryGetProperty("number", out var n) && n.GetInt32() == 404)
            {
                return Results.Json(new { data = new { repository = new { pullRequest = (object?)null } }, errors = new[] { new { type = "NOT_FOUND", message = "Could not resolve" } } });
            }

            // A pending review with more than a page of comments: pull request 7 ends on page 2 with the
            // 101st; pull request 8 never ends, past any bound.
            object PendingPage(int from, int count, bool more) => new
            {
                pageInfo = new { hasNextPage = more, endCursor = more ? $"p{from + count - 1}" : null },
                nodes = Enumerable.Range(from, count).Select(i => new { id = $"PRRC_{i}", path = "a.st", body = $"draft {i}" }).ToArray(),
            };
            if (query.Contains("reviews(states: PENDING", StringComparison.Ordinal))
            {
                return Results.Json(new { data = new { repository = new { pullRequest = new { reviews = new { nodes = new[]
                {
                    new { id = "PRR_" + variables.GetProperty("number").GetInt32(), viewerDidAuthor = true, author = new { login = "alice" }, commit = new { oid = new string('d', 40) }, comments = PendingPage(1, 100, true) },
                } } } } } });
            }

            if (query.Contains("on PullRequestReview { comments", StringComparison.Ordinal))
            {
                var endless = variables.GetProperty("id").GetString() == "PRR_8";
                var from = int.Parse(variables.GetProperty("after").GetString()![1..], System.Globalization.CultureInfo.InvariantCulture) + 1;
                return Results.Json(new { data = new { node = new { comments = endless ? PendingPage(from, 100, true) : PendingPage(from, 1, false) } } });
            }

            if (query.Contains("node(id:", StringComparison.Ordinal))
            {
                return Results.Json(new { data = new { node = new { comments = new { pageInfo = new { hasNextPage = false, endCursor = (string?)null }, nodes = new[] { Comment("C3") } } } } });
            }

            if (query.Contains("timelineItems", StringComparison.Ordinal))
            {
                var avatar = "https://avatars.githubusercontent.com/u/7";
                return Results.Json(new { data = new { repository = new { pullRequest = new { timelineItems = new
                {
                    pageInfo = new { hasNextPage = false, endCursor = (string?)null },
                    nodes = new object[]
                    {
                        new { __typename = "IssueComment", id = "IC_1", author = new { login = "jo", avatarUrl = avatar }, body = "fine", createdAt = "2026-10-17T09:00:00Z", lastEditedAt = "2026-10-17T09:30:00Z",
                            url = "https://github.com/acme/plant/pull/7#issuecomment-1", authorAssociation = "MEMBER", viewerCanUpdate = true, viewerCanDelete = false,
                            reactionGroups = new object[] { new { content = "THUMBS_UP", viewerHasReacted = true, reactors = new { totalCount = 2 } }, new { content = "EYES", viewerHasReacted = false, reactors = new { totalCount = 0 } } } },
                        new { __typename = "PullRequestReview", id = "PRR_1", author = new { login = "sam", avatarUrl = "http://evil.example/a.png" }, body = "", state = "APPROVED", submittedAt = "2026-10-17T10:00:00Z", createdAt = "2026-10-17T09:59:00Z",
                            url = "https://github.com/acme/plant/pull/7#pullrequestreview-1", authorAssociation = "OWNER", commit = new { oid = new string('d', 40) } },
                        new { __typename = "PullRequestCommit", url = "https://github.com/acme/plant/pull/7/commits/" + new string('d', 40),
                            commit = new { oid = new string('d', 40), messageHeadline = "Fix", committedDate = "2026-10-17T08:00:00Z", author = new { name = "Jo Git", avatarUrl = avatar, user = new { login = "jo" } }, signature = new { isValid = true } } },
                        new { __typename = "LabeledEvent", actor = new { login = "sam" }, createdAt = "2026-10-17T08:10:00Z", label = new { name = "plc", color = "1A7F37" } },
                        new { __typename = "ReviewRequestedEvent", actor = new { login = "sam" }, createdAt = "2026-10-17T08:11:00Z", requestedReviewer = new { __typename = "Team", name = "controls" } },
                        new { __typename = "RenamedTitleEvent", actor = new { login = "sam" }, createdAt = "2026-10-17T08:12:00Z", previousTitle = "fix", currentTitle = "Fix interlock" },
                        new { __typename = "HeadRefForcePushedEvent", actor = new { login = "jo" }, createdAt = "2026-10-17T08:13:00Z", beforeCommit = new { oid = new string('a', 40) }, afterCommit = new { oid = new string('d', 40) } },
                        new { __typename = "CrossReferencedEvent", actor = new { login = "lee" }, createdAt = "2026-10-17T08:14:00Z", willCloseTarget = true,
                            source = new { __typename = "Issue", number = 3, title = "Interlock trips late", url = "https://github.com/acme/plant/issues/3", state = "OPEN", repository = new { nameWithOwner = "acme/plant" } } },
                        new { __typename = "ReviewDismissedEvent", actor = new { login = "sam" }, createdAt = "2026-10-17T08:15:00Z", dismissalMessage = "stale", review = new { author = new { login = "lee" } } },
                        new { __typename = "MergedEvent", actor = new { login = "sam" }, createdAt = "2026-10-17T11:00:00Z", commit = new { oid = new string('9', 40) }, mergeRefName = "main" },
                        new { __typename = "SomethingNewerEvent", actor = new { login = "sam" }, createdAt = "2026-10-17T11:01:00Z" },
                    },
                } } } } });
            }

            if (query.Contains("reviewThreads", StringComparison.Ordinal))
            {
                var second = variables.TryGetProperty("after", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String;
                var thread = second
                    ? (object)new { id = "T2", path = "b.st", isResolved = true, isOutdated = true, line = (int?)null, startLine = (int?)null, originalLine = 4, diffSide = "LEFT", subjectType = "LINE",
                        comments = new { pageInfo = new { hasNextPage = false, endCursor = (string?)null }, nodes = new[] { Comment("C9") } } }
                    : new { id = "T1", path = "a.st", isResolved = false, isOutdated = false, line = 3, startLine = 2, originalLine = 3, diffSide = "RIGHT", subjectType = "LINE",
                        comments = new { pageInfo = new { hasNextPage = true, endCursor = "c-cursor" }, nodes = new[] { Comment("C1"), Comment("C2") } } };
                // The revision the lines describe, in the same answer; pull request 9 moves between its two pages.
                var moved = second && variables.GetProperty("number").GetInt32() == 9;
                return Results.Json(new { data = new { repository = new { pullRequest = new
                {
                    headRefOid = moved ? new string('e', 40) : new string('d', 40),
                    baseRefName = "main",
                    baseRef = new { target = new { oid = new string('c', 40) } },
                    reviewThreads = new
                {
                    pageInfo = new { hasNextPage = !second, endCursor = second ? null : "t-cursor" },
                    nodes = new[] { thread },
                } } } } });
            }

            return Results.Json(new { data = new { repository = new { pullRequest = new
            {
                state = "OPEN", isDraft = false, merged = false, mergeable = "MERGEABLE", reviewDecision = (string?)null,
                headRefOid = new string('d', 40), baseRefName = "main", baseRef = new { target = new { oid = new string('c', 40) } },
                labels = new { nodes = new[] { new { name = "plc", color = "FF0000" }, new { name = "odd", color = "red;x" } } },
                assignees = new { nodes = new[] { new { login = "jo" } } },
                reviewRequests = new { nodes = new object[] { new { requestedReviewer = new { __typename = "Team", name = "controls", slug = "plc-controls" } } } },
                viewerSubscription = "UNSUBSCRIBED", viewerCanReact = true,
                latestOpinionatedReviews = new { nodes = new[] { new { author = new { login = "sam" }, state = "APPROVED" } } },
                additions = 12, deletions = 3, changedFiles = 2, totalCommentsCount = 4, createdAt = "2026-10-15T10:00:00Z", locked = true, activeLockReason = "RESOLVED",
                isCrossRepository = true, authorAssociation = "MEMBER",
                author = new { login = "jo", avatarUrl = "https://avatars.githubusercontent.com/u/7?v=4" },
                milestone = new { title = "v2", number = 5 },
                headRepository = new { nameWithOwner = "fork/plant" },
                commits = new { totalCount = 3, nodes = new[] { new { commit = new { statusCheckRollup = new { state = "FAILURE", contexts = new { nodes = new object[]
                {
                    new { __typename = "CheckRun", name = "build", isRequired = true },
                    new { __typename = "StatusContext", context = "legacy", isRequired = false },
                } } } } } } },
                reactionGroups = new object[] { new { content = "THUMBS_UP", viewerHasReacted = false, reactors = new { totalCount = 2 } }, new { content = "EYES", viewerHasReacted = false, reactors = new { totalCount = 0 } } },
            } } } });
        });

        // A pull request's commits, in GitHub's shape: a matched login with a picture, a signature,
        // a committer who is not the author, two parents, a body after the headline.
        app.MapGet("/repos/acme/plant/pulls/7/commits", () => Results.Json(new object[]
        {
            new
            {
                sha = new string('d', 40), html_url = "https://github.com/acme/plant/commit/" + new string('d', 40),
                author = new { login = "jo", avatar_url = "https://avatars.githubusercontent.com/u/7?v=4" },
                parents = new[] { new { sha = new string('a', 40) }, new { sha = new string('b', 40) } },
                commit = new
                {
                    message = "Fix interlock\n\nThe delay was wrong.\n",
                    author = new { name = "Jo Git", date = "2026-10-17T08:00:00Z" },
                    committer = new { name = "GitHub", date = "2026-10-17T08:00:00Z" },
                    verification = new { verified = true },
                },
            },
        }));

        // Checks with an ETag: a request carrying it gets 304, as GitHub answers an unchanged poll.
        app.MapGet("/repos/acme/plant/commits/{sha}/check-runs", (string sha, HttpContext http) =>
        {
            // Paged as GitHub pages them: 100 passing runs, and the failing one on page 2 — or, for
            // the other commit, far more than any bound reads.
            if (sha is ['f', ..] or ['b', ..])
            {
                var page = int.Parse(http.Request.Query["page"].ToString() is { Length: > 0 } p ? p : "1", System.Globalization.CultureInfo.InvariantCulture);
                var etag = $"\"pg-{sha[0]}-{page}\"";
                if (http.Request.Headers.IfNoneMatch.ToString() == etag)
                {
                    Interlocked.Increment(ref _notModified);
                    return Results.StatusCode(304);
                }

                http.Response.Headers.ETag = etag;
                var total = sha[0] == 'f' ? 101 : 5000;
                var runs = Enumerable.Range((page - 1) * 100, Math.Max(0, Math.Min(100, total - ((page - 1) * 100))))
                    .Select(i => new { name = $"run {i}", status = "completed", conclusion = i == 100 ? "failure" : "success", html_url = "https://ci.example/r" })
                    .ToArray();
                return Results.Json(new { total_count = total, check_runs = runs });
            }

            if (http.Request.Headers.IfNoneMatch.ToString() == "\"runs-1\"")
            {
                Interlocked.Increment(ref _notModified);
                return Results.StatusCode(304);
            }

            http.Response.Headers.ETag = "\"runs-1\"";
            return Results.Json(new { check_runs = new[] { new
            {
                id = 42L, name = "build", status = "completed", conclusion = "success", html_url = "https://ci.example/1",
                started_at = "2026-10-17T08:00:00Z", completed_at = "2026-10-17T08:04:00Z", app = new { name = "GitHub Actions", slug = "github-actions" },
                check_suite = new { id = 7L }, output = new { title = "Built", summary = "All **green**", annotations_count = 3 },
            } } });
        });
        app.MapGet("/repos/acme/plant/commits/{sha}", (string sha) => Results.Json(new { sha, parents = new[] { new { sha = new string('a', 40) }, new { sha = new string('b', 40) } }, commit = new { message = "Apply suggestions\n\n<!-- versio:op v1 K -->" } }));
        app.MapGet("/repos/acme/plant/milestones", () => Results.Json(new object[] { new { number = 5, title = "v2", state = "open" }, new { number = 6, title = "v3", state = "open" } }));
        // Step 3b: a commit's comments (one on a line of a file, one on the commit) and a run's annotations.
        app.MapGet("/repos/acme/plant/commits/" + new string('u', 40) + "/comments", () => Results.Json(new object[]
        {
            new { node_id = "CC_1", user = new { login = "sam", avatar_url = "https://avatars.githubusercontent.com/u/1" }, body = "On the commit", created_at = "2026-10-24T09:00:00Z", path = (string?)null, line = (int?)null, html_url = "https://github.com/acme/plant/commit/u#commitcomment-1" },
            new { node_id = "CC_2", user = new { login = "lee", avatar_url = (string?)null }, body = "On a line", created_at = "2026-10-24T09:01:00Z", path = (string?)"src/Pump.st", line = (int?)2, html_url = "http://insecure.example/x" },
        }));
        app.MapGet("/repos/acme/plant/check-runs/42/annotations", () => Results.Json(new object[]
        {
            new { path = "src/Pump.st", start_line = 2, end_line = 2, annotation_level = "failure", message = "unknown identifier", title = "C0046" },
            new { path = "src/Pump.st", start_line = 5, end_line = (int?)null, annotation_level = "", message = "odd", title = "" },
        }));
        app.MapGet("/repos/acme/plant/commits/{sha}/status", () => Results.Json(new
        {
            statuses = new[] { new { context = "legacy", state = "pending", target_url = "javascript:alert(1)", description = "Waiting on the rig", creator = new { login = "jenkins-bot" }, created_at = "2026-10-17T08:01:00Z" } },
        }));
        app.MapGet("/repos/acme/plant/releases", () => Results.Json(new object[]
        {
            new
            {
                tag_name = "v2", name = (string?)null, draft = false, prerelease = true,
                author = new { login = "jo" }, published_at = (string?)"2026-10-01T00:00:00Z", created_at = "2026-09-30T00:00:00Z",
            },
            new
            {
                tag_name = "next", name = (string?)"unpublished", draft = true, prerelease = false,
                author = new { login = "jo" }, published_at = (string?)null, created_at = "2026-10-02T00:00:00Z",
            },
        }));
        app.MapGet("/repos/acme/plant/releases/tags/v2", () => Results.Json(new
        {
            tag_name = "v2", name = (string?)null, draft = false, prerelease = true,
            author = new { login = "jo" }, published_at = (string?)"2026-10-01T00:00:00Z", created_at = "2026-09-30T00:00:00Z",
            body = "## Notes",
        }));
        app.MapGet("/repos/acme/plant/git/trees/{sha}", (string sha) => Results.Json(new
        {
            sha,
            truncated = false,
            tree = new object[]
            {
                new { path = "src", type = "tree", sha = new string('d', 40) },
                new { path = "src/Main.st", type = "blob", sha = new string('a', 40), size = 13 },
                new { path = "big.bin", type = "blob", sha = new string('b', 40), size = 99 },
                new { path = "lfs.xml", type = "blob", sha = new string('e', 40), size = 130 },
            },
        }));
        app.MapGet("/repos/acme/plant/git/blobs/{sha}", (string sha, HttpContext http) =>
        {
            Assert.Equal("application/vnd.github.raw", http.Request.Headers.Accept.ToString());
            return sha[0] switch
            {
                'a' => Results.Text("PROGRAM Main\n", "application/vnd.github.raw"),
                'e' => Results.Text($"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 20\n", "application/vnd.github.raw"),
                'f' => Results.Text($"version https://git-lfs.github.com/spec/v1\noid sha256:{RealOid}\nsize {LfsContent.Length}\n", "application/vnd.github.raw"),
                _ => Results.Text(new string('x', 99), "application/vnd.github.raw"),
            };
        });
        app.MapPost("/acme/plant.git/info/lfs/objects/batch", () =>
            Results.Json(new { objects = new[] { new { oid = Oid, size = 20, actions = new { download = new { href = _lfsHref } } } } }));
        app.MapGet("/repos/acme/sso", (HttpContext http) =>
        {
            http.Response.Headers["X-GitHub-SSO"] = "required; url=https://github.com/orgs/acme/sso";
            return Results.StatusCode(403);
        });
        app.MapGet("/repos/acme/limited", (HttpContext http) =>
        {
            http.Response.Headers["x-ratelimit-remaining"] = "0";
            return Results.StatusCode(403);
        });
        app.MapGet("/repos/acme/forbidden", () => Results.StatusCode(403));
        app.MapGet("/repos/acme/held/installation", async () =>
        {
            // GitHub slow to answer for one repository.
            await _releaseHeld.Task;
            return Results.Json(new { id = 77 });
        });
        app.MapGet("/repos/acme/plant/installation", (HttpContext http) =>
        {
            _appJwts.Enqueue(http.Request.Headers.Authorization.ToString());
            return Results.Json(new { id = 77 });
        });
        app.MapPost("/app/installations/77/access_tokens", async (HttpContext http) =>
        {
            _appJwts.Enqueue(http.Request.Headers.Authorization.ToString());
            _mintBodies.Enqueue(await new StreamReader(http.Request.Body).ReadToEndAsync());
            return Results.Json(new { token = "installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) });
        });

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

    private GitHubRepoSource Source()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient).ConfigurePrimaryHttpMessageHandler(LfsGuard.CreateHandler);
        var options = Options.Create(new WebOptions { GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), WebBaseUrl = _address.ToString().TrimEnd('/') } });
        return new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);
    }

    private static readonly RepoAccess Alice = new(AccessKind.User, "alice-token", "alice");

    [Fact]
    public async Task A_repository_its_commit_and_its_tree_are_read_from_GitHubs_shapes()
    {
        var source = Source();

        var repo = await source.GetRepoAsync(Alice, "acme", "plant", CancellationToken.None);
        var commit = await source.ResolveCommitAsync(Alice, "acme", "plant", "main", CancellationToken.None);
        var tree = await source.GetTreeAsync(Alice, "acme", "plant", commit.Value!, CancellationToken.None);

        Assert.Equal(new RepoInfo("acme", "plant", false, "main"), repo.Value);
        Assert.Equal(new string('c', 40), commit.Value);
        Assert.Equal(["src/Main.st", "big.bin", "lfs.xml"], tree.Value!.Files.Select(f => f.Path));
        Assert.Equal(13, tree.Value.Files[0].Size);
        Assert.All(_requests, r => Assert.Equal("Bearer alice-token", r.Auth));
    }

    [Fact]
    public async Task Pulls_and_releases_are_read_from_GitHubs_shapes()
    {
        var source = Source();

        var pulls = await source.GetPullsAsync(Alice, "acme", "plant", 51, CancellationToken.None);
        var pull = await source.GetPullAsync(Alice, "acme", "plant", 7, CancellationToken.None);
        var releases = await source.GetReleasesAsync(Alice, "acme", "plant", 51, CancellationToken.None);
        var release = await source.GetReleaseAsync(Alice, "acme", "plant", "v2", CancellationToken.None);

        // One open pull, because the server answers only state=open with a row.
        var p = Assert.Single(pulls.Value!);
        Assert.Equal(new PullInfo(7, "Fix interlock", "jo", new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero), "main", "fix-interlock", new string('d', 40), true), p);
        Assert.Equal("", pull.Value!.Body);

        // The draft is never listed; the untitled release is shown by its tag.
        var r = Assert.Single(releases.Value!);
        Assert.Equal(new ReleaseInfo("v2", "v2", "jo", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), true), r);
        Assert.Equal("## Notes", release.Value!.Body);
    }

    [Fact]
    public async Task The_merge_base_is_read_from_the_three_dot_compare_and_must_be_a_commit_id()
    {
        var source = Source();

        var found = await source.GetMergeBaseAsync(Alice, "acme", "plant", new string('a', 40), new string('d', 40), CancellationToken.None);
        var malformed = await source.GetMergeBaseAsync(Alice, "acme", "plant", new string('a', 40), new string('e', 40), CancellationToken.None);

        // Lower-cased, as every other commit id the server hands on is.
        Assert.Equal(new string('b', 40), found.Value);
        Assert.Equal(RepoFailure.Upstream, malformed.Failure);
        Assert.Contains(_requests, r => r.Path == $"/repos/acme/plant/compare/{new string('a', 40)}...{new string('d', 40)}?per_page=1");
    }

    [Fact]
    public async Task A_pull_requests_files_are_read_across_GitHubs_pages_with_renames_and_missing_patches()
    {
        var source = Source();

        var files = await source.GetPullFilesAsync(Alice, "acme", "plant", 7, CancellationToken.None);

        Assert.Equal(150, files.Value!.Files.Count);
        Assert.False(files.Value.Truncated);
        Assert.Equal(new PullFile("src/New.st", "renamed", "src/Old.st", null), files.Value.Files[0]);
        Assert.Equal("@@ -1 +1 @@\n-a\n+b", files.Value.Files[1].Patch);
    }

    /// <summary>GitHub lists no further than 30 pages; a thirtieth full page is said, not passed off as the whole change.</summary>
    [Fact]
    public async Task A_pull_request_past_GitHubs_3000_file_cap_is_reported_cut()
    {
        var source = Source();

        var files = await source.GetPullFilesAsync(Alice, "acme", "plant", 8, CancellationToken.None);

        Assert.Equal(3000, files.Value!.Files.Count);
        Assert.True(files.Value.Truncated);
        Assert.Equal(30, _requests.Count(r => r.Path.StartsWith("/repos/acme/plant/pulls/8/files", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_compares_files_stop_at_GitHubs_300_and_the_cap_is_reported_cut()
    {
        var source = Source();

        var some = await source.GetCompareFilesAsync(Alice, "acme", "plant", new string('a', 40), new string('d', 40), CancellationToken.None);
        var full = await source.GetCompareFilesAsync(Alice, "acme", "plant", "full" + new string('a', 36), new string('d', 40), CancellationToken.None);

        Assert.Equal(2, some.Value!.Files.Count);
        Assert.False(some.Value.Truncated);
        Assert.Equal(300, full.Value!.Files.Count);
        Assert.True(full.Value.Truncated);
    }

    /// <summary>Both levels of paging: a second page of threads, and a thread whose comments overflow into a node query.</summary>
    [Fact]
    public async Task Review_threads_are_read_to_completion_across_both_levels_of_GitHubs_paging()
    {
        var source = Source();

        var threads = await source.GetReviewThreadsAsync(Alice, "acme", "plant", 7, CancellationToken.None);

        Assert.False(threads.Value!.Truncated);
        Assert.Equal(["T1", "T2"], threads.Value.Threads.Select(t => t.Id));
        Assert.Equal(["C1", "C2", "C3"], threads.Value.Threads[0].Comments.Select(c => c.Id));
        Assert.Equal((3, 2, "RIGHT"), (threads.Value.Threads[0].Line!.Value, threads.Value.Threads[0].StartLine!.Value, threads.Value.Threads[0].Side));
        Assert.True(threads.Value.Threads[1].Outdated);
        Assert.Null(threads.Value.Threads[1].Line);
        Assert.Equal("LEFT", threads.Value.Threads[1].Side);
    }

    [Fact]
    public async Task A_GraphQL_not_found_inside_a_200_is_not_found_and_no_token_means_no_query()
    {
        var source = Source();

        var missing = await source.GetPullSummaryAsync(Alice, "acme", "plant", 404, CancellationToken.None);
        var anonymous = await source.GetReviewThreadsAsync(RepoAccess.Anonymous, "acme", "plant", 7, CancellationToken.None);

        Assert.Equal(RepoFailure.NotFound, missing.Failure);
        Assert.Equal(RepoFailure.Unauthorized, anonymous.Failure);
        Assert.Single(_graphQueries);
    }

    [Fact]
    public async Task The_summary_reads_live_head_and_base_tip_with_reviewers_requested_or_reviewed_and_safe_label_colours()
    {
        var source = Source();

        var summary = (await source.GetPullSummaryAsync(Alice, "acme", "plant", 7, CancellationToken.None)).Value!;

        Assert.Equal(new string('d', 40), summary.HeadSha);
        Assert.Equal(new string('c', 40), summary.BaseTipSha);
        // A team is asked and withdrawn by its slug, which REST wants, and shown by its name.
        Assert.Equal([new PullReviewer("sam", "APPROVED"), new PullReviewer("controls", "REQUESTED", IsTeam: true) { Slug = "plc-controls" }], summary.Reviewers);
        // A colour is six hex digits or nothing: it ends up in a style attribute on the page.
        Assert.Equal([new PullLabel("plc", "ff0000"), new PullLabel("odd", "")], summary.Labels);
    }

    /// <summary>What Step 5's writes need read back: the milestone's number, the viewer's notifications, whether they may react; and the milestones themselves.</summary>
    [Fact]
    public async Task The_summary_reads_the_milestone_number_the_subscription_and_the_milestones_list_with_a_commits_message()
    {
        var summary = (await Source().GetPullSummaryAsync(Alice, "acme", "plant", 7, CancellationToken.None)).Value!;
        var milestones = (await Source().GetMilestonesAsync(Alice, "acme", "plant", CancellationToken.None)).Value!;
        var message = (await Source().GetCommitMessageAsync(Alice, "acme", "plant", new string('u', 40), CancellationToken.None)).Value;

        Assert.Equal((5, "UNSUBSCRIBED", true), (summary.MilestoneNumber, summary.ViewerSubscription, summary.ViewerCanReact));
        Assert.Equal([new MilestoneInfo(5, "v2", "open"), new MilestoneInfo(6, "v3", "open")], milestones);
        Assert.Equal("Apply suggestions\n\n<!-- versio:op v1 K -->", message);
    }

    /// <summary>Step 3b's reads: a commit's comments with a line comment's place, https links only; a run's annotations with GitHub's blanks filled in.</summary>
    [Fact]
    public async Task A_commits_comments_and_a_runs_annotations_are_read_in_their_shape()
    {
        var comments = (await Source().GetCommitCommentsAsync(Alice, "acme", "plant", new string('u', 40), CancellationToken.None)).Value!;
        var annotations = (await Source().GetCheckAnnotationsAsync(Alice, "acme", "plant", 42, CancellationToken.None)).Value!;

        Assert.Equal(
        [
            new CommitComment("CC_1", "sam", "https://avatars.githubusercontent.com/u/1", "On the commit", new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero), null, null, "https://github.com/acme/plant/commit/u#commitcomment-1"),
            // A line comment names its file and line; a link that is not https is dropped, as every link is.
            new CommitComment("CC_2", "lee", null, "On a line", new DateTimeOffset(2026, 10, 24, 9, 1, 0, TimeSpan.Zero), "src/Pump.st", 2, null),
        ], comments.Comments);
        Assert.False(comments.Truncated);
        Assert.Equal(
        [
            new CheckAnnotation("src/Pump.st", 2, 2, "failure", "unknown identifier", "C0046"),
            // No end line means the start line; no level means a notice; an empty title is none.
            new CheckAnnotation("src/Pump.st", 5, 5, "notice", "odd", null),
        ], annotations);
    }

    /// <summary>
    /// What GitHub's header and sidebar show, read from the summary (FINISH.md, B1 and B12): the
    /// counts, the author with a picture only from GitHub's avatar host, the lock, the milestone,
    /// a fork's repository, which of the live head's checks branch protection requires, and the
    /// reactions that have anyone in them.
    /// </summary>
    [Fact]
    public async Task The_summary_reads_the_header_counts_the_author_the_required_checks_and_the_reactions()
    {
        var summary = (await Source().GetPullSummaryAsync(Alice, "acme", "plant", 7, CancellationToken.None)).Value!;

        Assert.Equal((12, 3, 2, 3, 4), (summary.Additions, summary.Deletions, summary.ChangedFiles, summary.CommitCount, summary.CommentCount));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero), summary.CreatedAt);
        Assert.Null(summary.MergedAt);
        Assert.Equal(("jo", "https://avatars.githubusercontent.com/u/7?v=4", "MEMBER"), (summary.Author, summary.AuthorAvatar, summary.AuthorAssociation));
        Assert.Equal((true, "RESOLVED", "v2", "fork/plant"), (summary.Locked, summary.LockReason, summary.Milestone, summary.HeadRepository));
        Assert.Equal("FAILURE", summary.CheckRollup);
        Assert.Equal(["build"], summary.RequiredChecks);
        // A group nobody is in is not shown; the one with people is, with its count.
        Assert.Equal([new ReactionGroup("THUMBS_UP", 2, false)], summary.Reactions);
    }

    /// <summary>
    /// Every kind GitHub's conversation shows, read in its own shape (FINISH.md, B3), each with
    /// what it named; a kind this server does not know is dropped, never drawn as a raw word. A
    /// picture from anywhere but GitHub's avatar host is dropped: the page may not load it.
    /// </summary>
    [Fact]
    public async Task The_timeline_reads_every_event_kind_with_what_it_named_and_drops_the_unknown()
    {
        var items = (await Source().GetTimelineAsync(Alice, "acme", "plant", 7, CancellationToken.None)).Value!.Items;

        Assert.Equal(["comment", "review", "commit", "labeled", "review-requested", "renamed", "force-push", "cross-referenced", "review-dismissed", "merged"], items.Select(i => i.Kind).ToList());
        var comment = items[0];
        Assert.Equal(("IC_1", "MEMBER", "https://avatars.githubusercontent.com/u/7", "https://github.com/acme/plant/pull/7#issuecomment-1"), (comment.Id, comment.AuthorAssociation, comment.AvatarUrl, comment.Url));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.Zero), comment.EditedAt);
        Assert.Equal([new ReactionGroup("THUMBS_UP", 2, true)], comment.Reactions);
        var review = items[1];
        Assert.Equal(("PRR_1", "APPROVED", new string('d', 40), "OWNER"), (review.Id, review.State, review.Sha, review.AuthorAssociation));
        Assert.Null(review.AvatarUrl);
        Assert.Equal((true, "jo"), (items[2].Verified, items[2].Actor));
        Assert.Equal(("plc", "1a7f37"), (items[3].Target, items[3].TargetColor));
        Assert.Equal("controls", items[4].Target);
        Assert.Equal(("fix", "Fix interlock"), (items[5].Previous, items[5].Target));
        Assert.Equal((new string('a', 40), new string('d', 40)), (items[6].Previous, items[6].Sha));
        Assert.Equal(new TimelineReference(3, "Interlock trips late", "acme/plant", "https://github.com/acme/plant/issues/3", false, "OPEN"), items[7].Reference);
        Assert.Equal("closes", items[7].Message);
        Assert.Equal(("lee", "stale"), (items[8].Target, items[8].Message));
        Assert.Equal(("main", new string('9', 40)), (items[9].Target, items[9].Sha));
    }

    /// <summary>
    /// A pull request's commits carry what GitHub's commits tab shows (FINISH.md, B4): the matched
    /// login and picture, the signature, a committer who is not the author, the parents, the body.
    /// </summary>
    [Fact]
    public async Task The_commits_read_the_author_the_signature_the_committer_the_parents_and_the_body()
    {
        var commit = Assert.Single((await Source().GetPullCommitsAsync(Alice, "acme", "plant", 7, CancellationToken.None)).Value!.Commits);

        Assert.Equal(("Fix interlock", "jo", "jo", "https://avatars.githubusercontent.com/u/7?v=4"), (commit.Headline, commit.Author, commit.AuthorLogin, commit.AuthorAvatar));
        Assert.Equal(("GitHub", "The delay was wrong.", true, 2), (commit.Committer, commit.Body, commit.Verified, commit.ParentCount));
        Assert.Equal("https://github.com/acme/plant/commit/" + new string('d', 40), commit.Url);
    }

    /// <summary>
    /// Polled every 30 s: the second read sends the ETag, gets 304, and returns the kept answer,
    /// which costs nothing against the rate limit. A link that is not https is dropped.
    /// </summary>
    [Fact]
    public async Task Checks_are_polled_with_their_ETag_and_only_https_links_survive()
    {
        var source = Source();

        var first = await source.GetChecksAsync(Alice, "acme", "plant", new string('d', 40), CancellationToken.None);
        var second = await source.GetChecksAsync(Alice, "acme", "plant", new string('d', 40), CancellationToken.None);

        Assert.Equal(first.Value!.Checks, second.Value!.Checks);
        var checks = first.Value.Checks;
        Assert.Equal("https://ci.example/1", checks[0].Url);
        Assert.Equal(("legacy", "pending", (string?)null, (string?)null), (checks[1].Name, checks[1].Status, checks[1].Conclusion, checks[1].Url));
        // What GitHub's checks tab shows of a run (FINISH.md, B5): who ran it, when, what it said.
        Assert.Equal((42L, "GitHub Actions", 7L, "Built", "All **green**", 3), (checks[0].Id, checks[0].App, checks[0].Suite, checks[0].Title, checks[0].Summary, checks[0].AnnotationCount));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 8, 4, 0, TimeSpan.Zero), checks[0].CompletedAt);
        Assert.Equal(("Waiting on the rig", "jenkins-bot"), (checks[1].Description, checks[1].App));
        Assert.False(first.Value.Truncated);
        Assert.Equal(2, _requests.Count(r => r.Path.Contains("/check-runs", StringComparison.Ordinal)));
        Assert.Equal(1, _notModified);
    }

    /// <summary>
    /// Found in review (2026-10-20): 100 passing check runs and one failing one on page 2 showed as
    /// all green. Every page is read; the failure is there.
    /// </summary>
    [Fact]
    public async Task A_failing_check_on_the_second_page_is_read()
    {
        var checks = (await Source().GetChecksAsync(Alice, "acme", "plant", new string('f', 40), CancellationToken.None)).Value!;

        Assert.Equal(101 + 1, checks.Checks.Count);
        Assert.Contains(checks.Checks, c => c.Conclusion == "failure");
        Assert.False(checks.Truncated);
    }

    /// <summary>Past the bound, the list is said to be cut: never a partial list passed off as all of them.</summary>
    [Fact]
    public async Task Checks_past_the_bound_are_reported_cut()
    {
        var checks = (await Source().GetChecksAsync(Alice, "acme", "plant", new string('b', 40), CancellationToken.None)).Value!;

        Assert.Equal((GitHubRepoSource.MaxCheckPages * 100) + 1, checks.Checks.Count);
        Assert.True(checks.Truncated);
    }

    [Fact]
    public async Task Every_branch_is_listed_across_GitHubs_pages_up_to_the_cap()
    {
        // Found in review: one page of 100 was read, so branches past it were missing.
        var source = Source();

        var some = await source.GetBranchesAsync(Alice, "acme", "plant", CancellationToken.None);
        var endless = await source.GetBranchesAsync(Alice, "acme", "endless", CancellationToken.None);

        Assert.Equal(250, some.Value!.Branches.Count);
        Assert.Equal("b249", some.Value.Branches[^1].Name);
        Assert.False(some.Value.Truncated);
        Assert.Equal(3, _requests.Count(r => r.Path.StartsWith("/repos/acme/plant/branches", StringComparison.Ordinal)));
        Assert.Equal(GitHubRepoSource.BranchPages * GitHubRepoSource.BranchPageSize, endless.Value!.Branches.Count);
        Assert.True(endless.Value.Truncated);
        // The cap's pages, and one past it to learn whether the list was cut.
        Assert.Equal(GitHubRepoSource.BranchPages + 1, _requests.Count(r => r.Path.StartsWith("/repos/acme/endless/branches", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_blob_is_its_raw_bytes_and_one_over_the_cap_is_refused()
    {
        var source = Source();

        var small = await source.GetBlobAsync(Alice, "acme", "plant", new string('a', 40), 1000, CancellationToken.None);
        var big = await source.GetBlobAsync(Alice, "acme", "plant", new string('b', 40), 50, CancellationToken.None);

        Assert.Equal("PROGRAM Main\n", Encoding.UTF8.GetString(small.Value!));
        Assert.Equal(RepoFailure.TooLarge, big.Failure);
    }

    [Fact]
    public async Task An_LFS_download_pointed_at_the_metadata_service_is_refused_before_any_connection()
    {
        // Security rule 4: the LFS server's answer names 169.254.169.254; nothing is fetched.
        var source = Source();

        var result = await source.GetBlobAsync(Alice, "acme", "plant", new string('e', 40), 1000, CancellationToken.None);

        Assert.Equal(RepoFailure.Upstream, result.Failure);
        Assert.Contains(_requests, r => r.Path.Contains("lfs/objects/batch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_LFS_download_to_an_allowlisted_name_that_resolves_internal_is_refused_at_connect()
    {
        // The name passes the allowlist; the address it resolves to does not. localhost stands in
        // for a rebinding answer by giving the allowlisted host's handler a loopback target.
        _lfsHref = "https://localhost/media/x";
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient).ConfigurePrimaryHttpMessageHandler(LfsGuard.CreateHandler);
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), WebBaseUrl = _address.ToString().TrimEnd('/'), LfsHosts = ["localhost"] },
        });
        var source = new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);

        var result = await source.GetBlobAsync(Alice, "acme", "plant", new string('e', 40), 1000, CancellationToken.None);

        Assert.Equal(RepoFailure.Upstream, result.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_LFS_download_is_the_file_only_when_it_matches_its_pointer(bool tampered)
    {
        // Found in review: the downloaded bytes were never checked against the pointer's SHA-256,
        // so whatever the download host answered was drawn as this commit's file.
        _lfsHref = "https://lfs.example/media/x";
        var answer = tampered ? Encoding.UTF8.GetBytes("<Document>the fake file</Document>\n") : LfsContent;
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient).ConfigurePrimaryHttpMessageHandler(() => new LfsStore(answer));
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), WebBaseUrl = _address.ToString().TrimEnd('/'), LfsHosts = ["lfs.example"] },
        });
        var source = new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);

        var result = await source.GetBlobAsync(Alice, "acme", "plant", new string('f', 40), 1000, CancellationToken.None);

        if (tampered)
        {
            Assert.Equal(RepoFailure.Upstream, result.Failure);
        }
        else
        {
            Assert.Equal(LfsContent, result.Value);
        }
    }

    [Theory]
    [InlineData("sso", RepoFailure.SsoRequired)]
    [InlineData("limited", RepoFailure.RateLimited)]
    [InlineData("forbidden", RepoFailure.NotFound)]
    [InlineData("missing", RepoFailure.NotFound)]
    public async Task GitHubs_refusals_become_what_the_page_can_say(string repo, RepoFailure expected)
    {
        var result = await Source().GetRepoAsync(Alice, "acme", repo, CancellationToken.None);

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task An_answer_in_the_wrong_shape_is_upstream_not_an_exception()
    {
        var result = await Source().GetRepoAsync(Alice, "acme", "broken", CancellationToken.None);

        Assert.Equal(RepoFailure.Upstream, result.Failure);
    }

    [Fact]
    public async Task An_unreachable_GitHub_is_upstream_for_json_and_for_blobs()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient);
        var options = Options.Create(new WebOptions { GitHub = new GitHubOptions { ApiBaseUrl = "http://127.0.0.1:1", WebBaseUrl = "http://127.0.0.1:1" } });
        var source = new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);

        Assert.Equal(RepoFailure.Upstream, (await source.GetRepoAsync(Alice, "acme", "plant", CancellationToken.None)).Failure);
        Assert.Equal(RepoFailure.Upstream, (await source.GetBlobAsync(Alice, "acme", "plant", new string('a', 40), 100, CancellationToken.None)).Failure);
        Assert.Equal(RepoFailure.Upstream, (await source.ResolveCommitAsync(Alice, "acme", "plant", "main", CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task One_repositorys_slow_mint_does_not_hold_up_another_repositorys()
    {
        // Found in review: one lock serialised all minting, held across GitHub's round trips, so
        // one slow answer stalled every anonymous visitor to every repository.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), AppId = "123", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
        });
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        var tokens = new Versio.Web.Auth.AppTokens(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options,
            new Versio.Core.Abstractions.FakeClock(DateTimeOffset.UtcNow), new Versio.Web.Auth.PemAppSigner(options));

        var held = tokens.ForRepositoryAsync("acme", "held", CancellationToken.None);
        await Task.Delay(100);
        var other = await tokens.ForRepositoryAsync("acme", "plant", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        _releaseHeld.SetResult();

        Assert.Equal("installation-token", other);
        Assert.Equal("installation-token", await held);
    }

    [Fact]
    public async Task An_installation_token_is_minted_for_one_repository_read_only_with_a_valid_app_jwt()
    {
        // Security rule 1's other half: even the App's own credential reads one repository, read
        // only. And the JWT that asks for it is RS256-signed by the App's key (through IAppSigner,
        // the seam a key vault plugs into) with this App's id, valid minutes, not hours.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), AppId = "123", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
        });
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        var clock = new Versio.Core.Abstractions.FakeClock(DateTimeOffset.UtcNow);
        var tokens = new Versio.Web.Auth.AppTokens(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, clock, new Versio.Web.Auth.PemAppSigner(options));

        var token = await tokens.ForRepositoryAsync("acme", "plant", CancellationToken.None);

        Assert.Equal("installation-token", token);
        var body = System.Text.Json.JsonDocument.Parse(Assert.Single(_mintBodies)).RootElement;
        Assert.Equal(["plant"], body.GetProperty("repositories").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("read", body.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Equal("read", body.GetProperty("permissions").GetProperty("metadata").GetString());
        Assert.Equal(2, body.GetProperty("permissions").EnumerateObject().Count());

        foreach (var header in _appJwts)
        {
            var parts = header["Bearer ".Length..].Split('.');
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
            var claims = System.Text.Json.JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
            Assert.Equal("123", claims.GetProperty("iss").GetString());
            Assert.True(claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64() <= 600);
        }
    }

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }

    [Fact]
    public async Task A_name_cannot_reshape_the_url()
    {
        await Source().GetRepoAsync(Alice, "acme", "plant/../../evil?x=", CancellationToken.None);

        Assert.Contains(_requests, r => r.Path.StartsWith("/repos/acme/plant%2F..%2F..%2Fevil%3F", StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, r => r.Path.StartsWith("/evil", StringComparison.Ordinal));
    }

    /// <summary>
    /// Found in review (2026-10-20): only a pending review's first 100 comments were read, so the
    /// 101st was not shown and a draft's tag past it could not be found. Every page is read.
    /// </summary>
    [Fact]
    public async Task A_pending_reviews_comments_are_read_past_the_first_hundred()
    {
        var review = (await Source().GetPendingReviewAsync(Alice, "acme", "plant", 7, CancellationToken.None)).Value!.Review!;

        Assert.Equal(101, review.Comments.Count);
        Assert.Equal("draft 101", review.Comments[^1].Body);
        Assert.False(review.Truncated);
    }

    [Fact]
    public async Task Past_the_bound_a_pending_review_says_its_comments_were_cut()
    {
        var review = (await Source().GetPendingReviewAsync(Alice, "acme", "plant", 8, CancellationToken.None)).Value!.Review!;

        Assert.Equal(GitHubRepoSource.PendingCommentPages * 100, review.Comments.Count);
        Assert.True(review.Truncated);
    }

    [Fact]
    public async Task A_commits_parents_are_read_first_parent_first()
    {
        var parents = await Source().GetCommitParentsAsync(Alice, "acme", "plant", new string('u', 40), CancellationToken.None);

        Assert.Equal([new string('a', 40), new string('b', 40)], parents.Value!);
    }

    /// <summary>Every page of a long checks list is polled with its own ETag: an unchanged poll is all 304s (found in review, 2026-10-20).</summary>
    [Fact]
    public async Task An_unchanged_poll_of_a_long_checks_list_is_all_not_modified()
    {
        var source = Source();
        var first = await source.GetChecksAsync(Alice, "acme", "plant", new string('f', 40), CancellationToken.None);
        var before = _notModified;

        var again = await source.GetChecksAsync(Alice, "acme", "plant", new string('f', 40), CancellationToken.None);

        Assert.Equal(first.Value!.Checks, again.Value!.Checks);
        Assert.Equal(before + 2, _notModified);
    }

    /// <summary>
    /// The revision a thread's current lines are positions in is read in the same answers as the
    /// lines; when the pages disagree — the pull request moved mid-read — there is none.
    /// </summary>
    [Fact]
    public async Task Threads_carry_the_revision_their_lines_describe_and_none_when_it_moved_mid_read()
    {
        var steady = (await Source().GetReviewThreadsAsync(Alice, "acme", "plant", 7, CancellationToken.None)).Value!;
        var moved = (await Source().GetReviewThreadsAsync(Alice, "acme", "plant", 9, CancellationToken.None)).Value!;

        Assert.Equal(new PullRevision(new string('d', 40), "main", new string('c', 40)), steady.Revision);
        Assert.Null(moved.Revision);
    }
}
