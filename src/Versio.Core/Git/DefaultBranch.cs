namespace Versio.Core.Git;

/// <summary>Where a default-branch answer came from, which decides how much to trust it.</summary>
public enum DefaultBranchSource
{
    /// <summary>Nothing resolved. There is no answer.</summary>
    Unknown,

    /// <summary>Recorded in <c>.versio/repo.json</c> by whoever set it up.</summary>
    Configured,

    /// <summary>The remote's own <c>HEAD</c> says so.</summary>
    Remote,

    /// <summary>A branch named <c>main</c> or <c>master</c> exists locally.</summary>
    Conventional,
}

/// <summary>
/// Which branch is the trunk.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in this codebase knew.</b> Branch deletion needs it — deleting the branch you are
/// standing on means checking something else out first — and so does a merge request, which has
/// to name a target. Both would otherwise guess.
/// </para>
/// <para>
/// <b>The last step is a refusal, and that is the important one.</b> Guessing the trunk and then
/// checking it out is not a guess worth making: it moves somebody's working tree to a branch
/// nobody chose. When nothing resolves, the operations that need it are disabled and say why,
/// which is a worse experience and a recoverable one.
/// </para>
/// <para>
/// Pure, so every rung of the ladder is asserted without a repository. The IO — reading the
/// config, asking git for <c>origin/HEAD</c>, listing branches — belongs to the caller.
/// </para>
/// </remarks>
public sealed record DefaultBranch
{
    /// <summary>Nothing resolved.</summary>
    public static readonly DefaultBranch None = new()
    {
        Source = DefaultBranchSource.Unknown,
        Name = string.Empty,
    };

    public required DefaultBranchSource Source { get; init; }

    /// <summary>The branch name, or empty when nothing resolved.</summary>
    public required string Name { get; init; }

    public bool IsKnown => Source is not DefaultBranchSource.Unknown && Name.Length > 0;

    /// <summary>Why an operation that needs this cannot run, or empty when it can.</summary>
    /// <remarks>
    /// Names the setting, because the remedy is a one-line addition to a committed file and
    /// somebody who is told only "cannot determine" has nowhere to go.
    /// </remarks>
    public string BlockedReason =>
        IsKnown
            ? string.Empty
            : "Versio does not know which branch is this repository's main one. " +
              "Set it in .versio/repo.json, or make sure the remote has a default branch.";

    /// <summary>
    /// Work down the ladder: configuration, then the remote, then convention, then nothing.
    /// </summary>
    /// <param name="configured">
    /// <c>RepoConfig.DefaultBranch</c>. Committed and shared, so a team whose trunk is
    /// <c>develop</c> records it once and every clone agrees.
    /// </param>
    /// <param name="remoteHead">
    /// What <c>origin/HEAD</c> resolves to, in any of the shapes git reports it —
    /// <c>refs/remotes/origin/main</c>, <c>origin/main</c> or <c>main</c>.
    /// </param>
    /// <param name="localBranches">Every local branch, for the conventional fallback.</param>
    public static DefaultBranch Resolve(
        string? configured, string? remoteHead, IReadOnlyList<string> localBranches)
    {
        if (configured is { Length: > 0 } set && !string.IsNullOrWhiteSpace(set))
        {
            return new DefaultBranch
            {
                Source = DefaultBranchSource.Configured,
                Name = set.Trim(),
            };
        }

        if (ShortName(remoteHead) is { Length: > 0 } fromRemote)
        {
            return new DefaultBranch
            {
                Source = DefaultBranchSource.Remote,
                Name = fromRemote,
            };
        }

        // main before master, because a repository carrying both almost always renamed one and
        // kept the other for compatibility.
        foreach (var candidate in (string[])["main", "master"])
        {
            if (localBranches.Any(b => string.Equals(b, candidate, StringComparison.Ordinal)))
            {
                return new DefaultBranch
                {
                    Source = DefaultBranchSource.Conventional,
                    Name = candidate,
                };
            }
        }

        return None;
    }

    /// <summary>
    /// The branch name inside whatever shape git reported.
    /// </summary>
    /// <remarks>
    /// <c>symbolic-ref</c> answers <c>refs/remotes/origin/main</c>, while other commands answer
    /// <c>origin/main</c>. A branch name may itself contain slashes, so only the known prefixes
    /// are stripped rather than everything up to the last one — taking the last segment of
    /// <c>origin/release/2026</c> would answer <c>2026</c>.
    /// </remarks>
    private static string ShortName(string? reference)
    {
        var value = (reference ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        const string refsRemotes = "refs/remotes/";
        if (value.StartsWith(refsRemotes, StringComparison.Ordinal))
        {
            value = value[refsRemotes.Length..];
        }

        // Now "<remote>/<branch>". One slash separates them; the rest belong to the branch.
        var slash = value.IndexOf('/');
        return slash > 0 && slash < value.Length - 1 ? value[(slash + 1)..] : value;
    }
}
