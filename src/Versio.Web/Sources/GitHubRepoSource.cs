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

    public async Task<RepoResult<RepoInfo>> GetRepoAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}", ct).ConfigureAwait(false);
        if (!json.Ok)
        {
            return json.As<RepoInfo>();
        }

        var root = json.Value!.RootElement;
        return RepoResult<RepoInfo>.Of(new RepoInfo(
            root.GetProperty("owner").GetProperty("login").GetString() ?? owner,
            root.GetProperty("name").GetString() ?? repo,
            root.GetProperty("private").GetBoolean(),
            root.GetProperty("default_branch").GetString() ?? "main"));
    }

    public async Task<RepoResult<IReadOnlyList<BranchInfo>>> GetBranchesAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/branches?per_page=100", ct).ConfigureAwait(false);
        if (!json.Ok)
        {
            return json.As<IReadOnlyList<BranchInfo>>();
        }

        var branches = json.Value!.RootElement.EnumerateArray()
            .Select(b => new BranchInfo(b.GetProperty("name").GetString() ?? "", b.GetProperty("commit").GetProperty("sha").GetString() ?? ""))
            .ToList();
        return RepoResult<IReadOnlyList<BranchInfo>>.Of(branches);
    }

    public async Task<RepoResult<string>> ResolveCommitAsync(RepoAccess access, string owner, string repo, string reference, CancellationToken ct)
    {
        var json = await GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(reference)}", ct).ConfigureAwait(false);
        return json.Ok && json.Value!.RootElement.GetProperty("sha").GetString() is { Length: 40 } sha
            ? RepoResult<string>.Of(sha)
            : json.As<string>();
    }

    public async Task<RepoResult<TreeSnapshot>> GetTreeAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct)
    {
        var json = await GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/git/trees/{E(commitSha)}?recursive=1", ct).ConfigureAwait(false);
        if (!json.Ok)
        {
            return json.As<TreeSnapshot>();
        }

        var root = json.Value!.RootElement;
        var files = root.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => new TreeEntry(
                e.GetProperty("path").GetString() ?? "",
                e.GetProperty("sha").GetString() ?? "",
                e.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
            .ToList();
        var truncated = root.TryGetProperty("truncated", out var t) && t.GetBoolean();
        return RepoResult<TreeSnapshot>.Of(new TreeSnapshot(commitSha, files, truncated));
    }

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

        return await GetLfsObjectAsync(access, owner, repo, pointer, maxBytes, ct).ConfigureAwait(false);
    }

    public async Task<RepoResult<IReadOnlyList<CommitInfo>>> GetCommitsAsync(
        RepoAccess access, string owner, string repo, string reference, string? path, int limit, CancellationToken ct)
    {
        var query = $"repos/{E(owner)}/{E(repo)}/commits?sha={E(reference)}&per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}"
                    + (string.IsNullOrEmpty(path) ? "" : "&path=" + E(path));
        var json = await GetJsonAsync(access, query, ct).ConfigureAwait(false);
        if (!json.Ok)
        {
            return json.As<IReadOnlyList<CommitInfo>>();
        }

        var commits = json.Value!.RootElement.EnumerateArray().Select(c =>
        {
            var commit = c.GetProperty("commit");
            var author = commit.GetProperty("author");
            return new CommitInfo(
                c.GetProperty("sha").GetString() ?? "",
                commit.GetProperty("message").GetString() ?? "",
                author.GetProperty("name").GetString() ?? "",
                author.GetProperty("date").GetDateTimeOffset());
        }).ToList();
        return RepoResult<IReadOnlyList<CommitInfo>>.Of(commits);
    }

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

        try
        {
            return await SendForBytesAsync(clients.CreateClient(LfsClient), get, maxBytes, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            log.Warn(LogCategory.Web, $"LFS download for {owner}/{repo} refused or failed: {e.Message}");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream, "LFS download failed");
        }
    }

    private async Task<RepoResult<JsonDocument>> GetJsonAsync(RepoAccess access, string relative, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, Api(relative));
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            log.Warn(LogCategory.Web, $"GitHub unreachable for {Redact(relative)}: {e.Message}");
            return RepoResult<JsonDocument>.Fail(RepoFailure.Upstream);
        }

        using (response)
        {
            if (Failure(response) is { } failed)
            {
                return RepoResult<JsonDocument>.Fail(failed, Redact(relative));
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return RepoResult<JsonDocument>.Of(await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false));
        }
    }

    private static async Task<RepoResult<byte[]>> SendForBytesAsync(HttpClient client, HttpRequestMessage request, long maxBytes, CancellationToken ct)
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
