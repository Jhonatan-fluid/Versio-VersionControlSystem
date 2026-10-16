using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Versio.Core.Diff;
using Versio.Core.Iec.Ladder;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Core.Text;
using Versio.Web.Auth;
using Versio.Web.Rendering;
using Versio.Web.Repo;
using Versio.Web.Sources;

namespace Versio.Web.Api;

/// <summary>A tree row as the page draws it.</summary>
/// <param name="Name">For display: hidden characters spelled (security rule 9).</param>
/// <param name="Path">For navigation: exactly the file's path, which is what the API is asked for.</param>
/// <param name="Label">The path for display, spelled.</param>
public sealed record TreeNodeDto(string Name, string Path, string Label, string Kind, string? Primary, IReadOnlyList<string> Files, IReadOnlyList<TreeNodeDto> Children);

/// <summary>
/// The JSON API the page calls (docs/web/PLAN.md Step 2). Every route resolves its credential
/// through <see cref="AccessResolver"/> first and its commit with that credential second, and only
/// then touches a cache, so no answer is ever computed with, or served from, someone else's access
/// (security rules 1 and 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Inputs are checked before they reach GitHub</b>: owner and repository names to GitHub's own
/// alphabet, references and paths to printable text with no <c>..</c> segment. They are escaped
/// again when put in a URL; this is the first wall, that is the second.
/// </para>
/// <para>
/// <b>A refusal is a 404</b> unless there is something the person can do about it (sign in,
/// authorise SSO, wait out a rate limit), so the API never confirms that a private repository
/// exists to someone who cannot see it.
/// </para>
/// </remarks>
public static partial class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The server's repository list (PLAN.md, "The product shape"): the home page's "your
        // repositories". Each configured repository is checked with THIS caller's credential, so
        // what each person sees is the admin's scope intersected with their own access — one
        // they cannot read is left out entirely, never greyed out, because a row's absence must
        // say no more than a 404 would (security rule 1, applied to a list). An unscoped server
        // says so, and the page keeps its open-a-repository form instead.
        app.MapGet("/api/repos", async (HttpContext http, AccessResolver access, IRepoSource source,
            Microsoft.Extensions.Options.IOptions<WebOptions> options, CancellationToken ct) =>
        {
            var scope = options.Value.Repositories;
            if (scope.Length == 0)
            {
                return Results.Ok(new { scoped = false, repos = Array.Empty<object>() });
            }

            // Signed out on a sign-in-only server: the one refusal the person can act on.
            if (!options.Value.AnonymousAccess && await access.SessionAsync(http, ct).ConfigureAwait(false) is null)
            {
                return Refused(RepoFailure.Unauthorized, http);
            }

            // In parallel: the list is the admin's and short, and the checks are independent.
            // Entries were validated at startup (WebHost), so the split cannot fail here.
            var infos = await Task.WhenAll(scope.Select(async entry =>
            {
                var slash = entry.IndexOf('/');
                var (owner, repo) = (entry[..slash], entry[(slash + 1)..]);
                var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
                if (!who.Ok)
                {
                    return null;
                }

                var info = await source.GetRepoAsync(who.Value!, owner, repo, ct).ConfigureAwait(false);
                return info.Ok ? info.Value : null;
            })).ConfigureAwait(false);

            return Results.Ok(new
            {
                scoped = true,
                repos = infos.Where(i => i is not null)
                    .Select(i => new { owner = i!.Owner, name = i.Name, @private = i.Private, defaultBranch = i.DefaultBranch }),
            });
        }).RequireRateLimiting(WebHost.RatePolicy);

        var api = app.MapGroup("/api/repos/{owner}/{repo}").RequireRateLimiting(WebHost.RatePolicy);

        api.MapGet("", async (string owner, string repo, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!Valid(owner, repo))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return Refused(who.Failure, http);
            }

            var info = await source.GetRepoAsync(who.Value!, owner, repo, ct).ConfigureAwait(false);
            return info.Ok
                ? Results.Ok(new { owner = info.Value!.Owner, name = info.Value.Name, @private = info.Value.Private, defaultBranch = info.Value.DefaultBranch })
                : Refused(info.Failure, http);
        });

        api.MapGet("/branches", async (string owner, string repo, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!Valid(owner, repo))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return Refused(who.Failure, http);
            }

            var branches = await source.GetBranchesAsync(who.Value!, owner, repo, ct).ConfigureAwait(false);
            return branches.Ok
                ? Results.Ok(new { branches = branches.Value!.Branches.Select(b => new { name = b.Name, label = CodeHtml.Spelled(b.Name), commit = b.CommitSha }), truncated = branches.Value.Truncated })
                : Refused(branches.Failure, http);
        });

        api.MapGet("/tree", async (string owner, string repo, string? @ref, HttpContext http, Loader load, CancellationToken ct) =>
        {
            var model = await load.ModelAsync(http, owner, repo, @ref, ct).ConfigureAwait(false);
            if (!model.Ok)
            {
                return Refused(model.Failure, http);
            }

            var m = model.Value!.Model;
            return Results.Ok(new
            {
                commit = m.CommitSha,
                isVersio = m.IsVersio,
                truncated = m.Truncated,
                problems = m.Problems.Select(CodeHtml.Spelled),
                nodes = m.Nodes.Select(Dto),
            });
        });

        api.MapGet("/view", async (string owner, string repo, string? @ref, string? path, string? view, string? layout,
            HttpContext http, Loader load, ViewService views, IVersioLogger log, CancellationToken ct) =>
        {
            if (!ValidPath(path))
            {
                return Results.NotFound();
            }

            var model = await load.ModelAsync(http, owner, repo, @ref, ct).ConfigureAwait(false);
            if (!model.Ok)
            {
                return Refused(model.Failure, http);
            }

            var (access, m) = model.Value!;
            var result = await views.ViewAsync(access, m, path!, Pane(view), SideBySide(layout), ct).ConfigureAwait(false);
            Audit(log, access, m, path!, view);
            return result.Ok ? Results.Ok(result.Value) : Refused(result.Failure, http);
        });

        api.MapGet("/compare", async (string owner, string repo, string? @base, string? head, HttpContext http, Loader load, CancellationToken ct) =>
        {
            var older = await load.ModelAsync(http, owner, repo, @base, ct).ConfigureAwait(false);
            if (!older.Ok)
            {
                return Refused(older.Failure, http);
            }

            var newer = await load.ModelAsync(http, owner, repo, head, ct).ConfigureAwait(false);
            if (!newer.Ok)
            {
                return Refused(newer.Failure, http);
            }

            // Capped (security rule 8): two commits that differ in tens of thousands of files would
            // otherwise make one unbounded answer. The page says how many were left out.
            var all = ViewService.ChangedPaths(older.Value!.Model, newer.Value!.Model);
            var cap = Math.Max(1, http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebOptions>>().Value.MaxComparedFiles);
            var changes = all.Take(cap).ToList();
            return Results.Ok(new
            {
                @base = older.Value.Model.CommitSha,
                head = newer.Value.Model.CommitSha,
                total = all.Count,
                truncated = all.Count > changes.Count,
                files = changes.Select(c => new
                {
                    path = c.Path,
                    label = CodeHtml.Spelled(c.Path),
                    change = c.Change,
                    @object = (newer.Value.Model.NodeFor(c.Path) ?? older.Value.Model.NodeFor(c.Path)) is { Kind: ProjectNodeKind.Object } n ? CodeHtml.Spelled(n.Name) : null,
                }),
            });
        });

        api.MapGet("/compare/view", async (string owner, string repo, string? @base, string? head, string? path, string? view, string? layout,
            HttpContext http, Loader load, ViewService views, IVersioLogger log, CancellationToken ct) =>
        {
            if (!ValidPath(path))
            {
                return Results.NotFound();
            }

            var older = await load.ModelAsync(http, owner, repo, @base, ct).ConfigureAwait(false);
            if (!older.Ok)
            {
                return Refused(older.Failure, http);
            }

            var newer = await load.ModelAsync(http, owner, repo, head, ct).ConfigureAwait(false);
            if (!newer.Ok)
            {
                return Refused(newer.Failure, http);
            }

            var result = await views.CompareAsync(newer.Value!.Access, older.Value!.Model, newer.Value.Model, path!, Pane(view), SideBySide(layout), ct).ConfigureAwait(false);
            Audit(log, newer.Value.Access, newer.Value.Model, path!, $"compare {view}");
            return result.Ok ? Results.Ok(result.Value) : Refused(result.Failure, http);
        });

        api.MapGet("/history", async (string owner, string repo, string? @ref, string? path, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!Valid(owner, repo) || !ValidRef(@ref) || (path is not null && !ValidPath(path)))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return Refused(who.Failure, http);
            }

            var info = await source.GetRepoAsync(who.Value!, owner, repo, ct).ConfigureAwait(false);
            if (!info.Ok)
            {
                return Refused(info.Failure, http);
            }

            // The latest 50 shown, one GitHub page, on purpose: history here is for finding a recent
            // change, and older ones are on GitHub. One more is fetched: the page shows 50, says
            // "the latest 50" only when a 51st exists (a full page was the only signal, so exactly
            // 50 commits said it too; found in review), and the 51st gives the 50th its "what
            // changed" link. The page's HistoryLimit is the 50; change both together.
            var commits = await source.GetCommitsAsync(who.Value!, owner, repo, @ref ?? info.Value!.DefaultBranch, path, 51, ct).ConfigureAwait(false);
            return commits.Ok
                ? Results.Ok(commits.Value!.Select(c => new { sha = c.Sha, message = CodeHtml.Spelled(c.Message), author = CodeHtml.Spelled(c.Author), when = c.When }))
                : Refused(commits.Failure, http);
        });

        // A document's bytes, for the Documentation tab (PLAN.md, "The product shape" item 4):
        // the PDF the page embeds, and the download behind every Office row. Only paths the
        // Documents allowlist names, with the content type chosen HERE — never from the file —
        // and everything but PDF as an attachment; the reasoning is on Documents itself. The
        // credential, scope and commit rules are Loader's, same as every content route.
        api.MapGet("/raw", async (string owner, string repo, string? @ref, string? path,
            HttpContext http, Loader load, IRepoSource source, IVersioLogger log,
            Microsoft.Extensions.Options.IOptions<WebOptions> options, CancellationToken ct) =>
        {
            if (!ValidPath(path) || Documents.DispositionFor(path!) is not { } serve)
            {
                return Results.NotFound();
            }

            var model = await load.ModelAsync(http, owner, repo, @ref, ct).ConfigureAwait(false);
            if (!model.Ok)
            {
                return Refused(model.Failure, http);
            }

            var (access, m) = model.Value!;
            if (!m.Files.TryGetValue(path!, out var entry))
            {
                return Refused(RepoFailure.NotFound, http);
            }

            var bytes = await source.GetBlobAsync(access, owner, repo, entry.BlobSha, options.Value.MaxBlobBytes, ct).ConfigureAwait(false);
            if (!bytes.Ok)
            {
                return Refused(bytes.Failure, http);
            }

            Audit(log, access, m, path!, "raw");
            return serve.Inline
                ? Results.File(bytes.Value!, serve.ContentType)
                : Results.File(bytes.Value!, serve.ContentType, path![(path!.LastIndexOf('/') + 1)..]);
        });

        // The pulls and releases tabs (PLAN.md, "The product shape" item 5). Lists carry the meta
        // only; one body is fetched and rendered when picked, because fifty rendered Markdown
        // bodies per list load would be paid mostly for rows nobody opens. The view routes render
        // with Core's Markdown, exactly as a README is, so the page places them like any fragment.
        api.MapGet("/pulls", async (string owner, string repo, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!Valid(owner, repo))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return Refused(who.Failure, http);
            }

            // One more than shown, the history idiom: the 51st only says the list was cut.
            var pulls = await source.GetPullsAsync(who.Value!, owner, repo, ListLimit + 1, ct).ConfigureAwait(false);
            return pulls.Ok
                ? Results.Ok(new
                {
                    truncated = pulls.Value!.Count > ListLimit,
                    pulls = pulls.Value.Take(ListLimit).Select(p => new
                    {
                        number = p.Number,
                        title = CodeHtml.Spelled(p.Title),
                        author = CodeHtml.Spelled(p.Author),
                        when = p.Updated,
                        @base = p.BaseRef,
                        baseLabel = CodeHtml.Spelled(p.BaseRef),
                        head = p.HeadRef,
                        headLabel = CodeHtml.Spelled(p.HeadRef),
                        headSha = p.HeadSha,
                        draft = p.Draft,
                    }),
                })
                : Refused(pulls.Failure, http);
        });

        api.MapGet("/pulls/view", async (string owner, string repo, int? number, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!Valid(owner, repo) || number is not (> 0 and <= 100_000_000))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return Refused(who.Failure, http);
            }

            var pull = await source.GetPullAsync(who.Value!, owner, repo, number.Value, ct).ConfigureAwait(false);
            return pull.Ok
                ? Results.Ok(new
                {
                    number = pull.Value!.Pull.Number,
                    title = CodeHtml.Spelled(pull.Value.Pull.Title),
                    author = CodeHtml.Spelled(pull.Value.Pull.Author),
                    when = pull.Value.Pull.Updated,
                    @base = pull.Value.Pull.BaseRef,
                    baseLabel = CodeHtml.Spelled(pull.Value.Pull.BaseRef),
                    headLabel = CodeHtml.Spelled(pull.Value.Pull.HeadRef),
                    headSha = pull.Value.Pull.HeadSha,
                    draft = pull.Value.Pull.Draft,
                    html = MarkdownFragment(pull.Value.Body),
                })
                : Refused(pull.Failure, http);
        });

        api.MapGet("/releases", async (string owner, string repo, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!Valid(owner, repo))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return Refused(who.Failure, http);
            }

            var releases = await source.GetReleasesAsync(who.Value!, owner, repo, ListLimit + 1, ct).ConfigureAwait(false);
            return releases.Ok
                ? Results.Ok(new
                {
                    truncated = releases.Value!.Count > ListLimit,
                    releases = releases.Value.Take(ListLimit).Select(r => new
                    {
                        tag = r.Tag,
                        tagLabel = CodeHtml.Spelled(r.Tag),
                        name = CodeHtml.Spelled(r.Name),
                        author = CodeHtml.Spelled(r.Author),
                        when = r.When,
                        prerelease = r.Prerelease,
                    }),
                })
                : Refused(releases.Failure, http);
        });

        api.MapGet("/releases/view", async (string owner, string repo, string? tag, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!Valid(owner, repo) || tag is null || !ValidRef(tag))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return Refused(who.Failure, http);
            }

            var release = await source.GetReleaseAsync(who.Value!, owner, repo, tag, ct).ConfigureAwait(false);
            return release.Ok
                ? Results.Ok(new
                {
                    tag = release.Value!.Release.Tag,
                    tagLabel = CodeHtml.Spelled(release.Value.Release.Tag),
                    name = CodeHtml.Spelled(release.Value.Release.Name),
                    author = CodeHtml.Spelled(release.Value.Release.Author),
                    when = release.Value.Release.When,
                    prerelease = release.Value.Release.Prerelease,
                    html = MarkdownFragment(release.Value.Body),
                })
                : Refused(release.Failure, http);
        });

        // The renderers' stylesheets and script, from the constants in Core, so the page and the
        // desktop draw with the same CSS. Served, never inlined, for the CSP (script from 'self').
        // Five minutes, not a year: these names stay the same when a deploy changes their content.
        app.MapGet("/assets/code.css", (HttpContext h) => Asset(h, CodePageAssets.Css, "text/css"));
        app.MapGet("/assets/ladder.css", (HttpContext h) => Asset(h, LadderPageAssets.Css, "text/css"));
        app.MapGet("/assets/ladder.js", (HttpContext h) => Asset(h, LadderPageAssets.Script, "text/javascript"));

        // Mermaid, the desktop's vendored copy (see the csproj). Only the sandboxed diagram frame
        // loads it; the page itself never runs it (Http/Middleware.cs, the frame's policy).
        app.MapGet("/assets/mermaid.js", (HttpContext h) =>
        {
            h.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Bytes(Mermaid.Value, "text/javascript");
        });
    }

    private static readonly Lazy<byte[]> Mermaid = new(() =>
    {
        using var stream = typeof(ApiEndpoints).Assembly.GetManifestResourceStream("Versio.Web.mermaid.min.js")
                           ?? throw new InvalidOperationException("mermaid.min.js is not embedded in Versio.Web");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    private static IResult Asset(HttpContext http, string content, string type)
    {
        http.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Text(content, type);
    }

    /// <summary>Resolves a credential, then a commit, then the model: in that order, every time.</summary>
    public sealed class Loader(AccessResolver access, IRepoSource source, RepoModelLoader models, IMemoryCache cache)
    {
        /// <summary>How long a branch name keeps resolving to the commit it resolved to.</summary>
        public static readonly TimeSpan ResolutionLifetime = TimeSpan.FromSeconds(30);

        private readonly SingleFlight<RepoResult<string>> _resolving = new();

        /// <summary>
        /// A reference resolved to its commit with this credential, remembered for
        /// <see cref="ResolutionLifetime"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured on 2026-10-11: every API call resolved its branch again, two per page load and
        /// two more per click. Remembering the answer briefly saves those round trips; a push shows
        /// within half a minute, and a commit SHA in the URL never changes at all.
        /// </para>
        /// <para>
        /// <b>Keyed by the credential, so it shares nothing across people</b> (security rule 2):
        /// the resolution is what proves this caller can see this repository, and an answer proven
        /// with someone else's access must never stand in for it. The key holds a hash of the
        /// token, never the token.
        /// </para>
        /// </remarks>
        private async Task<RepoResult<string>> ResolveAsync(RepoAccess who, string owner, string repo, string reference, CancellationToken ct)
        {
            var credential = who.Token is null
                ? "anonymous"
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(who.Token)));
            var key = $"resolve:{credential}:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}:{reference}";
            if (cache.TryGetValue(key, out string? known) && known is not null)
            {
                return RepoResult<string>.Of(known);
            }

            var result = await _resolving.RunAsync(key, () => source.ResolveCommitAsync(who, owner, repo, reference, CancellationToken.None)).WaitAsync(ct).ConfigureAwait(false);
            if (result.Ok)
            {
                cache.Set(key, result.Value!, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ResolutionLifetime });
            }

            return result;
        }

        public async Task<RepoResult<(RepoAccess Access, RepoModel Model)>> ModelAsync(
            HttpContext http, string owner, string repo, string? reference, CancellationToken ct)
        {
            if (!Valid(owner, repo) || !ValidRef(reference))
            {
                return RepoResult<(RepoAccess, RepoModel)>.Fail(RepoFailure.NotFound);
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return who.As<(RepoAccess, RepoModel)>();
            }

            var target = reference;
            if (string.IsNullOrEmpty(target))
            {
                var info = await source.GetRepoAsync(who.Value!, owner, repo, ct).ConfigureAwait(false);
                if (!info.Ok)
                {
                    return info.As<(RepoAccess, RepoModel)>();
                }

                target = info.Value!.DefaultBranch;
            }

            var commit = await ResolveAsync(who.Value!, owner, repo, target, ct).ConfigureAwait(false);
            if (!commit.Ok)
            {
                return commit.As<(RepoAccess, RepoModel)>();
            }

            var model = await models.LoadAsync(who.Value!, owner, repo, commit.Value!, ct).ConfigureAwait(false);
            return model.Ok
                ? RepoResult<(RepoAccess, RepoModel)>.Of((who.Value!, model.Value!))
                : model.As<(RepoAccess, RepoModel)>();
        }
    }

    internal static IResult Refused(RepoFailure failure, HttpContext http) => failure switch
    {
        RepoFailure.Unauthorized => Results.Json(new { error = "sign-in", message = "Sign in to see this repository." }, statusCode: 401),
        RepoFailure.SsoRequired => Results.Json(new { error = "sso", message = "This organisation requires single sign-on. Authorise your GitHub session for it, then reload." }, statusCode: 403),
        RepoFailure.RateLimited => Results.Json(new { error = "rate-limit", message = "GitHub's rate limit is spent for now. Try again in a few minutes." }, statusCode: 429),
        RepoFailure.TooLarge => Results.Json(new { error = "too-large", message = "This is too large to show here." }, statusCode: 413),
        RepoFailure.Upstream => Results.Json(new { error = "upstream", message = "GitHub could not be reached. Try again shortly." }, statusCode: 502),
        _ => Results.Json(new { error = "not-found", message = "Not found, or not visible to you." }, statusCode: 404),
    };

    /// <summary>Rows per pulls or releases list: enough for a working set, bounded for the answer.</summary>
    internal const int ListLimit = 50;

    /// <summary>A pull request's description or a release's notes, rendered exactly as a README is.</summary>
    private static string MarkdownFragment(string markdown) =>
        markdown.Length == 0
            ? ""
            : "<div class=\"md-page\">" + MarkdownHtml.Fragment(Markdown.Parse(markdown)) + "</div>";

    internal static bool Valid(string owner, string repo) => Name().IsMatch(owner) && Name().IsMatch(repo) && repo is not ("." or "..");

    internal static bool ValidRef(string? reference) =>
        reference is null || (reference.Length is > 0 and <= 250 && RefChars().IsMatch(reference) && !reference.Contains("..", StringComparison.Ordinal));

    internal static bool ValidPath(string? path) =>
        path is { Length: > 0 and <= 1024 }
        && !path.StartsWith('/')
        && !path.Split('/').Any(s => s is "" or "." or "..")
        && !path.Any(char.IsControl);

    private static PaneView Pane(string? view) => string.Equals(view, "raw", StringComparison.OrdinalIgnoreCase) ? PaneView.Raw : PaneView.Rendered;

    private static bool SideBySide(string? layout) => string.Equals(layout, "split", StringComparison.OrdinalIgnoreCase);

    /// <summary>The audit trail of who viewed what (security rule 13). A person's login, never a token.</summary>
    private static void Audit(IVersioLogger log, RepoAccess access, RepoModel model, string path, string? view) =>
        log.Info(LogCategory.Web, $"view {access} {model.Owner}/{model.Repo}@{model.CommitSha[..Math.Min(8, model.CommitSha.Length)]} {path} {view ?? "rendered"}");

    private static TreeNodeDto Dto(ProjectTreeNode node) =>
        new(CodeHtml.Spelled(node.Name), node.Path, CodeHtml.Spelled(node.Path), node.Kind.ToString().ToLowerInvariant(), node.PrimaryFile, node.Files, node.Children.Select(Dto).ToList());

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex Name();

    [GeneratedRegex("^[A-Za-z0-9_./-]+$")]
    private static partial Regex RefChars();
}
