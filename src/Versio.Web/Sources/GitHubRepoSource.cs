using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;

namespace Versio.Web.Sources;

/// <summary>
/// Reads a repository through GitHub's REST API, following Git LFS pointers to their content.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trees and blobs, never a clone.</b> The Git Trees API lists a whole commit in one call and
/// the Blobs API returns one file by its hash, which is all a viewer needs. Nothing touches disk
/// and nothing starts a process (docs/web/PLAN.md Step 2; the Arch tests hold the server to it).
/// </para>
/// <para>
/// <b>A refusal is reported as "not found"</b> unless GitHub said precisely why. GitHub answers a
/// private repository's outsiders with 404 so its existence does not leak, and turning a bare 403
/// into anything more specific would undo that (security, hygiene).
/// </para>
/// </remarks>
public sealed class GitHubRepoSource(
    IHttpClientFactory clients,
    IOptions<WebOptions> options,
    IVersioLogger log) : IRepoSource
{
    /// <summary>The named client for the REST API and LFS batch calls.</summary>
    public const string ApiClient = "github";

    /// <summary>The named client for LFS downloads, built on <see cref="LfsGuard.CreateHandler"/>.</summary>
    public const string LfsClient = "github-lfs";

    private GitHubOptions GitHub => options.Value.GitHub;

    public Task<RepoResult<RepoInfo>> GetRepoAsync(RepoAccess access, string owner, string repo, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}", root => new RepoInfo(
            root.GetProperty("owner").GetProperty("login").GetString() ?? owner,
            root.GetProperty("name").GetString() ?? repo,
            root.GetProperty("private").GetBoolean(),
            root.GetProperty("default_branch").GetString() ?? "main"), ct);

    /// <remarks>
    /// Page by page, until a short page or <see cref="BranchPages"/>. The first version read one
    /// page of 100, so in a repository with more branches the rest were missing from the picker
    /// (found in review). The cap keeps a repository with thousands of branches from costing
    /// thousands of calls on every load; a ref past it still opens from its URL. A cut list says
    /// so (<see cref="BranchList.Truncated"/>): the first version stopped at the cap in silence, so
    /// a repository with more branches looked complete in the picker (found in review).
    /// </remarks>
    public async Task<RepoResult<BranchList>> GetBranchesAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var all = new List<BranchInfo>();
        for (var page = 1; page <= BranchPages; page++)
        {
            var one = await GetJsonAsync<IReadOnlyList<BranchInfo>>(access,
                $"repos/{E(owner)}/{E(repo)}/branches?per_page={BranchPageSize}&page={page.ToString(CultureInfo.InvariantCulture)}", root => root.EnumerateArray()
                    .Select(b => new BranchInfo(b.GetProperty("name").GetString() ?? "", b.GetProperty("commit").GetProperty("sha").GetString() ?? ""))
                    .ToList(), ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return one.As<BranchList>();
            }

            all.AddRange(one.Value!);
            if (one.Value!.Count < BranchPageSize)
            {
                return RepoResult<BranchList>.Of(new BranchList(all, false));
            }
        }

        // Every page full, up to the cap: there may be more, or exactly this many. One more page
        // decides, and is paid only by a repository this large.
        var past = await GetJsonAsync(access,
            $"repos/{E(owner)}/{E(repo)}/branches?per_page={BranchPageSize}&page={(BranchPages + 1).ToString(CultureInfo.InvariantCulture)}",
            root => root.GetArrayLength(), ct).ConfigureAwait(false);
        return RepoResult<BranchList>.Of(new BranchList(all, !past.Ok || past.Value > 0));
    }

    internal const int BranchPageSize = 100;

    internal const int BranchPages = 10;

    /// <remarks>
    /// Asks for <c>application/vnd.github.sha</c>, which answers with the forty characters and
    /// nothing else. The first version fetched the commit as JSON, which carries up to 300 files'
    /// patches, on every page load, to read one field (found reviewing).
    /// </remarks>
    public async Task<RepoResult<string>> ResolveCommitAsync(RepoAccess access, string owner, string repo, string reference, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, Api($"repos/{E(owner)}/{E(repo)}/commits/{E(reference)}"));
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.sha");
        var bytes = await SendForBytesAsync(clients.CreateClient(ApiClient), request, 1024, ct).ConfigureAwait(false);
        if (!bytes.Ok)
        {
            return bytes.As<string>();
        }

        var sha = Encoding.ASCII.GetString(bytes.Value!).Trim();
        return sha.Length == 40 && sha.All(Uri.IsHexDigit)
            ? RepoResult<string>.Of(sha.ToLowerInvariant())
            : RepoResult<string>.Fail(RepoFailure.Upstream, "not a commit id");
    }

    public Task<RepoResult<TreeSnapshot>> GetTreeAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/git/trees/{E(commitSha)}?recursive=1", root => new TreeSnapshot(
            commitSha,
            root.GetProperty("tree").EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() == "blob")
                .Select(e => new TreeEntry(
                    e.GetProperty("path").GetString() ?? "",
                    e.GetProperty("sha").GetString() ?? "",
                    e.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
                .ToList(),
            root.TryGetProperty("truncated", out var t) && t.GetBoolean()), ct);

    public async Task<RepoResult<byte[]>> GetBlobAsync(RepoAccess access, string owner, string repo, string blobSha, long maxBytes, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, Api($"repos/{E(owner)}/{E(repo)}/git/blobs/{E(blobSha)}"));
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.raw");
        var bytes = await SendForBytesAsync(clients.CreateClient(ApiClient), request, maxBytes, ct).ConfigureAwait(false);
        if (!bytes.Ok || LfsPointer.Parse(bytes.Value!) is not { } pointer)
        {
            return bytes;
        }

        // The blob is a pointer; the file is in LFS. A TIA project's export is (Open item 1).
        if (pointer.Size > maxBytes)
        {
            return RepoResult<byte[]>.Fail(RepoFailure.TooLarge, $"LFS object of {pointer.Size} bytes");
        }

        try
        {
            return await GetLfsObjectAsync(access, owner, repo, pointer, maxBytes, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            // The batch call itself failing, or answering in a shape LFS does not document.
            log.Warn(LogCategory.Web, $"LFS batch for {owner}/{repo} failed: {e.GetType().Name}");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream, "LFS batch failed");
        }
    }

    public async Task<RepoResult<IReadOnlyList<CommitInfo>>> GetCommitsAsync(
        RepoAccess access, string owner, string repo, string reference, string? path, int limit, CancellationToken ct)
    {
        var query = $"repos/{E(owner)}/{E(repo)}/commits?sha={E(reference)}&per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}"
                    + (string.IsNullOrEmpty(path) ? "" : "&path=" + E(path));
        return await GetJsonAsync<IReadOnlyList<CommitInfo>>(access, query, root => root.EnumerateArray().Select(c =>
        {
            var commit = c.GetProperty("commit");
            var author = commit.GetProperty("author");
            return new CommitInfo(
                c.GetProperty("sha").GetString() ?? "",
                commit.GetProperty("message").GetString() ?? "",
                author.GetProperty("name").GetString() ?? "",
                author.GetProperty("date").GetDateTimeOffset());
        }).ToList(), ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// Sorted by update, newest first: the list is for "what is waiting for review now". One page
    /// (the API's cap is 100); a repository with more open pull requests than that shows the
    /// newest and says the list was cut, as the branch list does.
    /// </remarks>
    public Task<RepoResult<IReadOnlyList<PullInfo>>> GetPullsAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<PullInfo>>(access,
            $"repos/{E(owner)}/{E(repo)}/pulls?state=open&sort=updated&direction=desc&per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}",
            root => root.EnumerateArray().Select(Pull).ToList(), ct);

    public Task<RepoResult<PullDetail>> GetPullAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}",
            root => new PullDetail(Pull(root), Text(root, "body")), ct);

    public Task<RepoResult<IReadOnlyList<ReleaseInfo>>> GetReleasesAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<ReleaseInfo>>(access,
            $"repos/{E(owner)}/{E(repo)}/releases?per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}",
            root => root.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean()).Select(Release).ToList(), ct);

    public Task<RepoResult<ReleaseDetail>> GetReleaseAsync(RepoAccess access, string owner, string repo, string tag, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/releases/tags/{E(tag)}",
            root => new ReleaseDetail(Release(root), Text(root, "body")), ct);

    private static PullInfo Pull(JsonElement p) => new(
        p.GetProperty("number").GetInt32(),
        p.GetProperty("title").GetString() ?? "",
        p.GetProperty("user").GetProperty("login").GetString() ?? "",
        p.GetProperty("updated_at").GetDateTimeOffset(),
        p.GetProperty("base").GetProperty("ref").GetString() ?? "",
        p.GetProperty("head").GetProperty("ref").GetString() ?? "",
        p.GetProperty("head").GetProperty("sha").GetString() ?? "",
        p.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True);

    private static ReleaseInfo Release(JsonElement r) => new(
        r.GetProperty("tag_name").GetString() ?? "",
        // An untitled release is shown by its tag, which GitHub's own page does too.
        Text(r, "name") is { Length: > 0 } name ? name : r.GetProperty("tag_name").GetString() ?? "",
        r.GetProperty("author").GetProperty("login").GetString() ?? "",
        // published_at is null while a release is a draft; drafts are filtered out, but the
        // fallback keeps a malformed answer from throwing.
        r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : r.GetProperty("created_at").GetDateTimeOffset(),
        r.GetProperty("prerelease").GetBoolean());

    /// <summary>A string property that may be missing or JSON null, as GitHub's optional bodies are.</summary>
    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private async Task<RepoResult<byte[]>> GetLfsObjectAsync(
        RepoAccess access, string owner, string repo, LfsPointer pointer, long maxBytes, CancellationToken ct)
    {
        var batch = new Uri($"{GitHub.WebBaseUrl.TrimEnd('/')}/{E(owner)}/{E(repo)}.git/info/lfs/objects/batch");
        using var request = new HttpRequestMessage(HttpMethod.Post, batch)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    operation = "download",
                    transfers = new[] { "basic" },
                    objects = new[] { new { oid = pointer.Oid, size = pointer.Size } },
                }),
                Encoding.UTF8,
                "application/vnd.git-lfs+json"),
        };
        request.Headers.Accept.ParseAdd("application/vnd.git-lfs+json");
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        if (access.Token is { } token)
        {
            // LFS takes basic auth; GitHub accepts an App or user token as the password.
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:" + token)));
        }

        using var response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
        if (Failure(response) is { } failed)
        {
            return RepoResult<byte[]>.Fail(failed, "LFS batch");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var obj = doc.RootElement.GetProperty("objects")[0];
        if (!obj.TryGetProperty("actions", out var actions) || !actions.TryGetProperty("download", out var download))
        {
            return RepoResult<byte[]>.Fail(RepoFailure.NotFound, "LFS object missing");
        }

        var href = download.GetProperty("href").GetString();
        if (!LfsGuard.IsAllowed(href, GitHub.LfsHosts))
        {
            log.Warn(LogCategory.Web, $"LFS download for {owner}/{repo} pointed at a host that is not allowlisted; refused (security rule 4)");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream, "LFS host not allowed");
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, href);
        if (download.TryGetProperty("header", out var headers))
        {
            foreach (var header in headers.EnumerateObject())
            {
                get.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }
        }

        // A refused connect (an internal address) or a failure is logged and reported as upstream
        // inside SendForBytesAsync.
        var content = await SendForBytesAsync(clients.CreateClient(LfsClient), get, maxBytes, ct).ConfigureAwait(false);
        if (!content.Ok)
        {
            return content;
        }

        // The pointer names its content by SHA-256 and size, and the pointer is what the commit
        // holds; the download comes from a host the LFS server chose. Bytes that do not match are
        // not this commit's file, whatever the reason (a cache, a proxy, a tampered store), and
        // were drawn as if they were until 2026-10-11 (found in review).
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Value!));
        if (content.Value!.LongLength != pointer.Size || !hash.Equals(pointer.Oid, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn(LogCategory.Web, $"LFS download for {owner}/{repo} does not match its pointer (oid {pointer.Oid[..12]}); refused");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream, "LFS content does not match its pointer");
        }

        return content;
    }

    /// <summary>A GET that reads its answer with <paramref name="read"/> and disposes the document.</summary>
    /// <remarks>
    /// <para>
    /// <b>The document never leaves this method.</b> A <see cref="JsonDocument"/> rents pooled
    /// memory until it is disposed; the first version returned it to callers that never disposed
    /// it (found reviewing). Handing in the reader makes forgetting impossible.
    /// </para>
    /// <para>
    /// <b>An answer that is not the shape GitHub documents is "upstream"</b>, the same as an
    /// unreachable GitHub: a missing property must be a 502 the page can word, never a 500.
    /// </para>
    /// </remarks>
    private async Task<RepoResult<T>> GetJsonAsync<T>(RepoAccess access, string relative, Func<JsonElement, T> read, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, Api(relative));
        try
        {
            using var response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
            if (Failure(response) is { } failed)
            {
                return RepoResult<T>.Fail(failed, Redact(relative));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return RepoResult<T>.Of(read(document.RootElement));
        }
        catch (HttpRequestException e)
        {
            log.Warn(LogCategory.Web, $"GitHub unreachable for {Redact(relative)}: {e.Message}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            log.Warn(LogCategory.Web, $"GitHub answered {Redact(relative)} in an unexpected shape: {e.GetType().Name}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
    }

    private async Task<RepoResult<byte[]>> SendForBytesAsync(HttpClient client, HttpRequestMessage request, long maxBytes, CancellationToken ct)
    {
        try
        {
            return await ReadBytesAsync(client, request, maxBytes, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            // Unreachable GitHub, or an LFS host the connect callback refused: a 502 the page can
            // word, never a 500. The first version let this escape from blob reads (found reviewing).
            log.Warn(LogCategory.Web, $"Fetching {request.RequestUri?.Host} failed: {e.Message}");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream);
        }
    }

    private static async Task<RepoResult<byte[]>> ReadBytesAsync(HttpClient client, HttpRequestMessage request, long maxBytes, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (Failure(response) is { } failed)
        {
            return RepoResult<byte[]>.Fail(failed);
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return RepoResult<byte[]>.Fail(RepoFailure.TooLarge);
        }

        // Read with a cap whatever the header claimed: a missing or lying Content-Length must not
        // let a response grow without bound.
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return RepoResult<byte[]>.Fail(RepoFailure.TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return RepoResult<byte[]>.Of(buffer.ToArray());
    }

    /// <summary>GitHub's answer as a failure, or null when it succeeded.</summary>
    internal static RepoFailure? Failure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        var exhausted = response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => RepoFailure.NotFound,
            HttpStatusCode.Unauthorized => RepoFailure.Unauthorized,
            HttpStatusCode.Forbidden when response.Headers.Contains("X-GitHub-SSO") => RepoFailure.SsoRequired,
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when exhausted => RepoFailure.RateLimited,
            HttpStatusCode.TooManyRequests => RepoFailure.RateLimited,
            HttpStatusCode.Forbidden => RepoFailure.NotFound,
            _ => RepoFailure.Upstream,
        };
    }

    private HttpRequestMessage Request(RepoAccess access, HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (access.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private Uri Api(string relative) => new($"{GitHub.ApiBaseUrl.TrimEnd('/')}/{relative}");

    /// <summary>A path segment or query value, escaped so a repository or path name cannot reshape the URL.</summary>
    private static string E(string value) => Uri.EscapeDataString(value);

    /// <summary>What a log line may say about a request: the path without its query.</summary>
    private static string Redact(string relative) => relative.Split('?')[0];
}
