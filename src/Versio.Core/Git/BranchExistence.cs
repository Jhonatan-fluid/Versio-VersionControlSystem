namespace Versio.Core.Git;

/// <summary>Where a branch actually exists.</summary>
public enum BranchExistence
{
    /// <summary>On this machine only. Nobody else can see the work on it.</summary>
    LocalOnly,

    /// <summary>On the remote only. Checking it out creates the local branch.</summary>
    RemoteOnly,

    /// <summary>Both, which is the ordinary state of a branch somebody is sharing.</summary>
    Both,
}

/// <summary>
/// Which branches exist, and where.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versio could not see a remote-only branch at all.</b> <c>BranchesAsync</c> read
/// <c>refs/heads</c>, which is local branches — so a colleague's branch, or one this clone had
/// never checked out, simply did not appear. The dropdown looked like the whole repository and
/// was half of it.
/// </para>
/// <para>
/// <b>Merged by short name, which is what a person means by "the same branch".</b>
/// <c>main</c> and <c>origin/main</c> are two refs and one branch as far as anybody choosing
/// one is concerned. The pairing is by name rather than by commit deliberately: two branches
/// that happen to point at the same commit are still two branches, and one that has diverged
/// from its own remote counterpart is still the same branch.
/// </para>
/// </remarks>
public static class BranchInventory
{
    /// <summary>
    /// Combine local and remote-tracking refs into one list.
    /// </summary>
    /// <param name="local">Branches from <c>refs/heads</c>, with their upstreams and counts.</param>
    /// <param name="remote">
    /// Short names from <c>refs/remotes</c>, still carrying their remote prefix — for example
    /// <c>origin/feature/x</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <c>origin/HEAD</c> is dropped. It is a symbolic ref naming the remote's default branch,
    /// not a branch of its own, and showing it would offer a checkout of something that is
    /// really an alias for a branch already in the list.
    /// </para>
    /// <para>
    /// Ordered current first, then local, then remote-only, each alphabetically. A repository
    /// with a busy remote can carry hundreds of remote-only branches and they must not bury the
    /// three somebody works on.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<BranchInfo> Combine(
        IReadOnlyList<BranchInfo> local, IReadOnlyList<string> remote)
    {
        var byName = new Dictionary<string, BranchInfo>(StringComparer.Ordinal);

        foreach (var branch in local)
        {
            byName[branch.Name] = branch with { Existence = BranchExistence.LocalOnly };
        }

        foreach (var reference in remote)
        {
            var slash = reference.IndexOf('/');
            if (slash <= 0 || slash == reference.Length - 1)
            {
                continue;
            }

            var name = reference[(slash + 1)..];

            // A symbolic ref for the remote's default, not a branch. Offering it would be a
            // checkout of an alias for something already listed.
            if (name == "HEAD")
            {
                continue;
            }

            if (byName.TryGetValue(name, out var existing))
            {
                byName[name] = existing with { Existence = BranchExistence.Both };
                continue;
            }

            byName[name] = new BranchInfo
            {
                Name = name,
                Existence = BranchExistence.RemoteOnly,
                RemoteRef = reference,
            };
        }

        return
        [
            .. byName.Values
                .OrderByDescending(b => b.IsCurrent)
                .ThenBy(b => b.Existence == BranchExistence.RemoteOnly)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
