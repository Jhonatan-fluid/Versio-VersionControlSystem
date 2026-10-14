using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Versio.Web.Sources;

namespace Versio.Web.Tests;

/// <summary>The regex timeout before any test builds a pattern, as Program.cs does before the host.</summary>
internal static class ProcessSetup
{
    [ModuleInitializer]
    internal static void Run()
    {
        WebHost.ConfigureProcess();
        WebHost.RegisterFormats();
    }
}

/// <summary>Where this repository is, so the fakes can serve its real fixtures.</summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Versio.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Versio.sln not found above the test output.");
    }

    /// <summary>Every file under <paramref name="relative"/>, repo-relative with forward slashes.</summary>
    public static IEnumerable<string> Files(string relative) =>
        Directory.EnumerateFiles(System.IO.Path.Combine(Path, relative), "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/'));
}

/// <summary>
/// A GitHub in memory: repositories made of commits made of files, each read recorded with the
/// credential it used.
/// </summary>
/// <remarks>
/// It answers as GitHub does where the security rules care: a credential that may not see a
/// private repository gets "not found", and the App's installation token CAN see a private
/// repository it is installed on, which is exactly the case the resolver must refuse.
/// </remarks>
internal sealed class FakeGitHub : IRepoSource
{
    public sealed class FakeRepo
    {
        public required string Owner { get; init; }

        public required string Name { get; init; }

        public bool Private { get; init; }

        public string DefaultBranch { get; init; } = "main";

        /// <summary>Branch or commit name to commit sha.</summary>
        public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);

        /// <summary>Commit sha to its files.</summary>
        public Dictionary<string, Dictionary<string, byte[]>> Commits { get; } = new(StringComparer.Ordinal);

        /// <summary>User tokens allowed to read it when private. The installation token always can.</summary>
        public HashSet<string> Readers { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, FakeRepo> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Repo, string What, RepoAccess Access)> Reads { get; } = new();

    /// <summary>Blobs whose read fails, and how: a rate limit, an outage, for as long as they stay listed.</summary>
    public ConcurrentDictionary<string, RepoFailure> FailingBlobs { get; } = new(StringComparer.Ordinal);

    public FakeRepo Add(string owner, string name, bool isPrivate = false)
    {
        var repo = new FakeRepo { Owner = owner, Name = name, Private = isPrivate };
        _repos[$"{owner}/{name}"] = repo;
        return repo;
    }

    /// <summary>Adds a commit and points <paramref name="branch"/> at it.</summary>
    public string Commit(FakeRepo repo, string branch, Dictionary<string, byte[]> files)
    {
        var sha = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(branch + string.Join(',', files.Keys.Order()) + files.Values.Sum(v => v.Length)))).ToLowerInvariant();
        repo.Commits[sha] = files;
        repo.Refs[branch] = sha;
        foreach (var bytes in files.Values)
        {
            _blobs[BlobSha(bytes)] = bytes;
        }

        return sha;
    }

    /// <summary>The files of the commit <paramref name="branch"/> points at.</summary>
    public Dictionary<string, byte[]> CommitFiles(FakeRepo repo, string branch) => repo.Commits[repo.Refs[branch]];

    /// <summary>The files of this repository under the given folders, as on disk.</summary>
    public static Dictionary<string, byte[]> FromDisk(params string[] folders)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (File.Exists(System.IO.Path.Combine(RepoRoot.Path, folder)))
            {
                files[folder] = File.ReadAllBytes(System.IO.Path.Combine(RepoRoot.Path, folder));
                continue;
            }

            foreach (var file in RepoRoot.Files(folder))
            {
                files[file] = File.ReadAllBytes(System.IO.Path.Combine(RepoRoot.Path, file));
            }
        }

        return files;
    }

    /// <summary>Git's blob id: SHA-1 of "blob {length}\0" and the bytes.</summary>
    public static string BlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes])).ToLowerInvariant();
    }

    public const string InstallationToken = "installation-token";

    private RepoResult<FakeRepo> See(RepoAccess access, string owner, string repo, string what)
    {
        Reads.Enqueue(($"{owner}/{repo}", what, access));
        if (!_repos.TryGetValue($"{owner}/{repo}", out var r))
        {
            return RepoResult<FakeRepo>.Fail(RepoFailure.NotFound);
        }

        var allowed = !r.Private
                      || access.Kind == AccessKind.Installation
                      || (access.Kind == AccessKind.User && access.Token is { } t && r.Readers.Contains(t));
        return allowed ? RepoResult<FakeRepo>.Of(r) : RepoResult<FakeRepo>.Fail(RepoFailure.NotFound);
    }

    public Task<RepoResult<RepoInfo>> GetRepoAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var r = See(access, owner, repo, "repo");
        return Task.FromResult(r.Ok ? RepoResult<RepoInfo>.Of(new RepoInfo(r.Value!.Owner, r.Value.Name, r.Value.Private, r.Value.DefaultBranch)) : r.As<RepoInfo>());
    }

    public Task<RepoResult<BranchList>> GetBranchesAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var r = See(access, owner, repo, "branches");
        return Task.FromResult(r.Ok
            ? RepoResult<BranchList>.Of(new BranchList(r.Value!.Refs.Select(kv => new BranchInfo(kv.Key, kv.Value)).ToList(), false))
            : r.As<BranchList>());
    }

    public Task<RepoResult<string>> ResolveCommitAsync(RepoAccess access, string owner, string repo, string reference, CancellationToken ct)
    {
        var r = See(access, owner, repo, "resolve " + reference);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<string>());
        }

        var sha = r.Value!.Refs.TryGetValue(reference, out var s) ? s : r.Value.Commits.ContainsKey(reference) ? reference : null;
        return Task.FromResult(sha is null ? RepoResult<string>.Fail(RepoFailure.NotFound) : RepoResult<string>.Of(sha));
    }

    /// <summary>Runs before a tree is read, with the credential reading it: may hold the read, or fail it.</summary>
    public Func<RepoAccess, Task<RepoFailure>>? BeforeTree { get; set; }

    public async Task<RepoResult<TreeSnapshot>> GetTreeAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct)
    {
        var r = See(access, owner, repo, "tree " + commitSha);
        if (!r.Ok || !r.Value!.Commits.TryGetValue(commitSha, out var files))
        {
            return RepoResult<TreeSnapshot>.Fail(RepoFailure.NotFound);
        }

        if (BeforeTree is { } before && await before(access) is var failure and not RepoFailure.None)
        {
            return RepoResult<TreeSnapshot>.Fail(failure);
        }

        return RepoResult<TreeSnapshot>.Of(new TreeSnapshot(
            commitSha, files.Select(f => new TreeEntry(f.Key, BlobSha(f.Value), f.Value.Length)).ToList(), false));
    }

    public Task<RepoResult<byte[]>> GetBlobAsync(RepoAccess access, string owner, string repo, string blobSha, long maxBytes, CancellationToken ct)
    {
        var r = See(access, owner, repo, "blob " + blobSha);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<byte[]>());
        }

        if (FailingBlobs.TryGetValue(blobSha, out var failure))
        {
            return Task.FromResult(RepoResult<byte[]>.Fail(failure));
        }

        // Only a blob that is in one of THIS repository's commits: a blob id is not a key to
        // every repository on the server.
        var inRepo = r.Value!.Commits.Values.Any(c => c.Values.Any(v => BlobSha(v) == blobSha));
        if (!inRepo || !_blobs.TryGetValue(blobSha, out var bytes))
        {
            return Task.FromResult(RepoResult<byte[]>.Fail(RepoFailure.NotFound));
        }

        return Task.FromResult(bytes.Length > maxBytes ? RepoResult<byte[]>.Fail(RepoFailure.TooLarge) : RepoResult<byte[]>.Of(bytes));
    }

    public Task<RepoResult<IReadOnlyList<CommitInfo>>> GetCommitsAsync(RepoAccess access, string owner, string repo, string reference, string? path, int limit, CancellationToken ct)
    {
        var r = See(access, owner, repo, "commits");
        return Task.FromResult(r.Ok
            ? RepoResult<IReadOnlyList<CommitInfo>>.Of(r.Value!.Refs.Select(kv => new CommitInfo(kv.Value, "commit " + kv.Key, "Tester", DateTimeOffset.UnixEpoch)).ToList())
            : r.As<IReadOnlyList<CommitInfo>>());
    }
}
