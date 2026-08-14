namespace Versio.Core.Git;

/// <summary>Which hosting product a remote belongs to, when that can be established.</summary>
/// <remarks>
/// <see cref="Unknown"/> is a real answer and the common one for a self-hosted instance. It is
/// not a failure and it is not a default: a forge that cannot be identified gets no button
/// rather than a guessed one, because the whole feature is a link and a wrong link is a 404
/// with Versio's name on it.
/// </remarks>
public enum ForgeKind
{
    Unknown,
    GitHub,
    GitLab,
    AzureDevOps,
    Bitbucket,
    Gitea,
}

/// <summary>
/// Where a branch's merge request lives on the web.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every URL here is pre-filled and none of them is submitted.</b> Versio opens a page with
/// the source and target branches already chosen; a human presses Create in the forge's own
/// interface. Nothing outward-facing happens on Versio's say-so, and that is a property of the
/// design rather than of the current implementation — there is no code path here that posts.
/// </para>
/// <para>
/// <b>Self-hosted instances cannot be identified by hostname.</b> A GitLab at
/// <c>git.acme.internal</c> looks like nothing in particular, so detection covers the four
/// hosts that are unambiguous and everything else comes from <c>repo.json</c> — committed, so a
/// team sets it once and every clone agrees. When neither answers, there is no button.
/// </para>
/// <para>
/// Pure: it takes a parsed <see cref="RemoteUrl"/> and returns strings, so all five forges and
/// both URL shapes are asserted without a network.
/// </para>
/// </remarks>
public static class Forge
{
    /// <summary>
    /// Which forge this remote belongs to.
    /// </summary>
    /// <param name="url">The remote, parsed.</param>
    /// <param name="configured">
    /// The <c>forge</c> value from <c>repo.json</c>, or <see cref="ForgeKind.Unknown"/>.
    /// </param>
    /// <remarks>
    /// <b>Configuration wins over the hostname, not the other way round.</b> A team that has
    /// said what their forge is has said something the hostname cannot contradict — an
    /// enterprise GitHub behind a CNAME of <c>github.acme.com</c>, or a mirror. The reverse
    /// precedence would make the setting useless in exactly the cases it exists for.
    /// </remarks>
    public static ForgeKind Detect(RemoteUrl url, ForgeKind configured = ForgeKind.Unknown)
    {
        if (configured is not ForgeKind.Unknown)
        {
            return configured;
        }

        // A local path is not a forge, whatever it is called.
        if (url.Transport is RemoteTransport.Local or RemoteTransport.Unknown)
        {
            return ForgeKind.Unknown;
        }

        return url.Host.ToLowerInvariant() switch
        {
            "github.com" or "www.github.com" => ForgeKind.GitHub,
            "gitlab.com" or "www.gitlab.com" => ForgeKind.GitLab,
            "dev.azure.com" or "ssh.dev.azure.com" => ForgeKind.AzureDevOps,
            "bitbucket.org" => ForgeKind.Bitbucket,

            // Deliberately no "codeberg.org" => Gitea and no *.gitlab.io: the first is a guess
            // about one instance of a product, and the second is GitLab's PAGES domain, which
            // is never a git remote. Both belong in repo.json if somebody needs them.
            _ => ForgeKind.Unknown,
        };
    }

    /// <summary>
    /// What this forge calls the thing: "pull request", or GitLab's "merge request".
    /// </summary>
    /// <remarks>
    /// The plan wrote "merge request" throughout, which is GitLab's word and wrong on the other
    /// four. A button that calls a GitHub pull request a merge request is a small thing that
    /// reads as a tool built by somebody who has not used the forge it is opening.
    /// </remarks>
    public static string Noun(ForgeKind kind) =>
        kind is ForgeKind.GitLab ? "merge request" : "pull request";

    /// <summary>
    /// The page for opening a new request from <paramref name="branch"/>, pre-filled.
    /// </summary>
    /// <returns>Empty when the forge is unknown, which is what hides the button.</returns>
    public static string CreateUrl(
        ForgeKind kind, RemoteUrl url, string branch, string targetBranch)
    {
        var root = WebRoot(url);

        // Deliberately does NOT test `kind is Unknown` here. That decision belongs to the
        // switch's default arm and to nothing else: with it guarded twice, the arm becomes
        // unreachable, and the mutation this rule is checked by — "default an unknown host to
        // GitHub's template" — passed the whole suite. A rule enforced in two places is a rule
        // whose test only reaches one of them.
        if (root.Length == 0 || branch.Length == 0 || targetBranch.Length == 0)
        {
            return string.Empty;
        }

        var source = Uri.EscapeDataString(branch);
        var target = Uri.EscapeDataString(targetBranch);

        return kind switch
        {
            // The three-dot form is the forge's own compare syntax, not a range: it means
            // "everything on branch that is not on target". Branch names are left unescaped in
            // a path segment because a slash in feature/x is a path separator to these two and
            // escaping it produces a 404.
            ForgeKind.GitHub => $"{root}/compare/{targetBranch}...{branch}?expand=1",
            ForgeKind.Gitea => $"{root}/compare/{targetBranch}...{branch}",

            ForgeKind.GitLab =>
                $"{root}/-/merge_requests/new" +
                $"?merge_request[source_branch]={source}" +
                $"&merge_request[target_branch]={target}",

            ForgeKind.AzureDevOps =>
                $"{root}/pullrequestcreate?sourceRef={source}&targetRef={target}",

            ForgeKind.Bitbucket =>
                $"{root}/pull-requests/new?source={source}&dest={target}",

            // The one place an unidentified forge is turned away, and the reason the guard
            // above does not also test for it.
            _ => string.Empty,
        };
    }

    /// <summary>One existing request, by the number a ref probe found.</summary>
    public static string ExistingUrl(ForgeKind kind, RemoteUrl url, int number)
    {
        var root = WebRoot(url);
        if (root.Length == 0 || number <= 0)
        {
            return string.Empty;
        }

        return kind switch
        {
            ForgeKind.GitHub => $"{root}/pull/{number}",
            ForgeKind.GitLab => $"{root}/-/merge_requests/{number}",
            ForgeKind.AzureDevOps => $"{root}/pullrequest/{number}",
            ForgeKind.Bitbucket => $"{root}/pull-requests/{number}",
            ForgeKind.Gitea => $"{root}/pulls/{number}",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The repository's request list.
    /// </summary>
    /// <remarks>
    /// <b>Where "go to the merge request" honestly cannot be answered.</b> Azure DevOps and
    /// Bitbucket publish no hidden refs, so there is no way to learn a number without an API
    /// token — and a link to a list is honest where a fabricated number is not.
    /// </remarks>
    public static string ListUrl(ForgeKind kind, RemoteUrl url)
    {
        var root = WebRoot(url);
        if (root.Length == 0)
        {
            return string.Empty;
        }

        return kind switch
        {
            ForgeKind.GitHub => $"{root}/pulls",
            ForgeKind.GitLab => $"{root}/-/merge_requests",
            ForgeKind.AzureDevOps => $"{root}/pullrequests",
            ForgeKind.Bitbucket => $"{root}/pull-requests",
            ForgeKind.Gitea => $"{root}/pulls",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The hidden ref namespace this forge publishes its request heads under, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes "does one already exist?" answerable without an API token.</b> Both
    /// major forges expose request heads as refs readable with the same credentials as a fetch,
    /// so one <c>ls-remote</c> answers it — where the obvious implementations are wrong:
    /// <c>.git/config</c> knows nothing about requests, and an authenticated REST call means
    /// storing a token, which is a security surface of its own for a link.
    /// </para>
    /// <para>
    /// <b>Empty for the other three, and Gitea is empty on purpose.</b> Gitea mirrors much of
    /// GitHub's ref layout and very likely publishes these too — but "very likely" is not a
    /// measurement, and the cost of being wrong is a probe that always misses and a button that
    /// silently never upgrades from Create to Go to. It costs an <c>ls-remote</c> to find out
    /// against a real instance; until somebody has, it degrades to the list page like Azure and
    /// Bitbucket.
    /// </para>
    /// </remarks>
    public static string RequestRefPattern(ForgeKind kind) => kind switch
    {
        ForgeKind.GitHub => "refs/pull/*/head",
        ForgeKind.GitLab => "refs/merge-requests/*/head",
        _ => string.Empty,
    };

    /// <summary>
    /// The request number whose head is <paramref name="tip"/>, or null.
    /// </summary>
    /// <param name="lsRemoteOutput">Raw <c>ls-remote</c> output: <c>sha\tref</c> per line.</param>
    /// <param name="tip">The local branch's commit id.</param>
    /// <remarks>
    /// <para>
    /// A match says a request exists whose head is this commit. It does <b>not</b> say whether
    /// that request is open, merged or a draft — which is why the button says "Go to pull
    /// request" and never "1 open pull request". Claiming a state the refs cannot support is
    /// how a link becomes a lie.
    /// </para>
    /// <para>
    /// The <b>highest</b> number wins where several match, because two requests can share a tip
    /// — reopened, or a second one raised from the same commit — and the newest is the one
    /// somebody means. Rare, and the consequence of getting it wrong is landing on a real
    /// adjacent request rather than on nothing.
    /// </para>
    /// </remarks>
    public static int? RequestNumber(string? lsRemoteOutput, string? tip)
    {
        if (string.IsNullOrWhiteSpace(lsRemoteOutput) || string.IsNullOrWhiteSpace(tip))
        {
            return null;
        }

        int? best = null;

        foreach (var line in lsRemoteOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals(tip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // refs/pull/123/head, refs/merge-requests/45/head — the number is the third
            // segment in both, which is why one parser covers both forges.
            var segments = parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 &&
                int.TryParse(segments[2], out var number) &&
                number > 0 &&
                number > (best ?? 0))
            {
                best = number;
            }
        }

        return best;
    }

    /// <summary>
    /// The repository's web address, or empty when the remote is not one that has one.
    /// </summary>
    /// <remarks>
    /// <b>Always https unless the remote itself is plain http.</b> An SSH remote —
    /// <c>git@github.com:owner/repo.git</c> — has a perfectly good web address, and it is not
    /// reached over ssh. Preserving http for an http remote matters for the self-hosted
    /// instance on a private network that never got a certificate; upgrading it to https would
    /// produce a link that cannot connect.
    /// </remarks>
    private static string WebRoot(RemoteUrl url)
    {
        if (url.Repository.Length == 0 ||
            url.Host.Length == 0 ||
            url.Transport is RemoteTransport.Local or RemoteTransport.Unknown)
        {
            return string.Empty;
        }

        var scheme = url.Protocol == "http" ? "http" : "https";

        // The owner is empty for a remote whose repository sits at the host root, and joining
        // through it unconditionally produces a double slash — which some forges follow and
        // others 404 on.
        var path = url.Owner.Length == 0
            ? url.Repository
            : $"{url.Owner.Trim('/')}/{url.Repository}";

        return $"{scheme}://{url.Authority}/{path}";
    }
}
