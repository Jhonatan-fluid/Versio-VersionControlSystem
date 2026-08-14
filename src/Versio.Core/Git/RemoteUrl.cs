namespace Versio.Core.Git;

/// <summary>How a remote is reached, which decides what a credential failure means.</summary>
public enum RemoteTransport
{
    /// <summary>Not a remote URL at all, or one in no recognisable shape.</summary>
    Unknown,

    /// <summary>http:// or https:// — a credential helper's business.</summary>
    Http,

    /// <summary>ssh:// or the scp-like <c>git@host:owner/repo</c> — keys and an agent.</summary>
    Ssh,

    /// <summary>A path on this machine or a share. No authentication at all.</summary>
    Local,
}

/// <summary>
/// A remote URL, taken apart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which transport a remote uses decides what advice is worth giving.</b> An HTTPS remote
/// that refuses you is a job for the credential helper; an SSH one has no helper at all — it is
/// keys and <c>ssh-agent</c> — and offering a "Sign in" button there would be offering a button
/// that cannot work. That is the same class of mistake as advising a pull on a branch that
/// <c>--ff-only</c> will refuse.
/// </para>
/// <para>
/// <b>The host is what a credential is scoped to.</b> Git stores credentials per protocol and
/// host, so that is what has to be handed to <c>git credential</c> — and it is what the
/// sign-in prompt names, because "sign in to github.com" is meaningful where "sign in" alone
/// is not.
/// </para>
/// <para>
/// <b>This is the second home for parsing that already existed.</b> The clone dialog had its
/// own copy for deriving a folder name from a URL, and two independent parsers of the same
/// notoriously irregular format would eventually disagree about a shape one of them had learned
/// and the other had not. <see cref="RepositoryName"/> is the behaviour that dialog needs, kept
/// here so there is one implementation.
/// </para>
/// </remarks>
public sealed record RemoteUrl
{
    /// <summary>The URL as configured, untouched.</summary>
    public required string Raw { get; init; }

    public required RemoteTransport Transport { get; init; }

    /// <summary>Scheme for <c>git credential</c>: <c>https</c>, <c>http</c> or <c>ssh</c>.
    /// Empty for a local path.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Host alone, with no port and no credentials in it.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// The port, when the URL carried one. Empty otherwise.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not part of <see cref="Host"/>, and deliberately not thrown away either.</b>
    /// Git scopes a stored credential to protocol and host, so the port must stay out of the
    /// key that <c>git credential</c> is handed — but a self-hosted forge on
    /// <c>https://git.acme.internal:8443/…</c> needs it back to build a link somebody can click,
    /// and dropping it silently produced a URL that resolves to a different service or to
    /// nothing. Two readers, two questions, one parse.
    /// </remarks>
    public string Port { get; init; } = string.Empty;

    /// <summary>Host and port as a URL authority: <c>git.acme.internal:8443</c>.</summary>
    public string Authority => Port.Length == 0 ? Host : $"{Host}:{Port}";

    /// <summary>Everything between the host and the repository name, slash-separated.</summary>
    /// <remarks>
    /// Kept whole rather than split into "owner", because forges disagree about how deep it
    /// goes: GitHub has one level, GitLab allows arbitrarily nested groups, and Azure DevOps
    /// puts an organisation and a project either side of a literal <c>_git</c>.
    /// </remarks>
    public string Owner { get; init; } = string.Empty;

    /// <summary>Repository name, with any <c>.git</c> suffix removed.</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>
    /// True when a credential helper could possibly help.
    /// </summary>
    /// <remarks>
    /// The one question callers actually ask. A matching <c>UsesSshKeys</c> read better and
    /// was never called: SSH advice comes from <see cref="GitAuthClassifier"/>, which reads
    /// git's own words rather than the URL shape, so the negation had no consumer.
    /// <see cref="Transport"/> is public for anything that needs the distinction.
    /// </remarks>
    public bool UsesCredentialHelper => Transport is RemoteTransport.Http;

    /// <summary>Nothing configured.</summary>
    public static readonly RemoteUrl None = new()
    {
        Raw = string.Empty,
        Transport = RemoteTransport.Unknown,
    };

    /// <summary>
    /// Take a configured remote apart.
    /// </summary>
    /// <remarks>
    /// Three shapes, which is every one git accepts in practice: a scheme URL
    /// (<c>https://host/owner/repo.git</c>), the scp-like form
    /// (<c>git@host:owner/repo.git</c>), and a path. Anything else is
    /// <see cref="RemoteTransport.Unknown"/> rather than a guess — a wrong guess here becomes a
    /// browser opened on a 404, or sign-in advice for a remote that never asks for credentials.
    /// </remarks>
    public static RemoteUrl Parse(string? url)
    {
        var raw = (url ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return None;
        }

        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            return FromSchemeUrl(raw, raw[..scheme].ToLowerInvariant(), raw[(scheme + 3)..]);
        }

        // The scp-like form: user@host:path, with a colon where a scheme URL has a slash. A
        // Windows drive letter also carries a colon, so the @ is what separates them.
        var at = raw.IndexOf('@');
        var colon = raw.IndexOf(':');
        if (at > 0 && colon > at)
        {
            var host = raw[(at + 1)..colon];
            var (owner, repository) = SplitPath(raw[(colon + 1)..]);

            return new RemoteUrl
            {
                Raw = raw,
                Transport = RemoteTransport.Ssh,
                Protocol = "ssh",
                Host = host,
                Owner = owner,
                Repository = repository,
            };
        }

        // A path: absolute, relative, or a UNC share. None of them authenticate.
        return new RemoteUrl
        {
            Raw = raw,
            Transport = RemoteTransport.Local,
            Repository = RepositoryName(raw),
        };
    }

    private static RemoteUrl FromSchemeUrl(string raw, string protocol, string rest)
    {
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? string.Empty : rest[(slash + 1)..];

        // Credentials embedded in the URL are stripped before the host is recorded: they must
        // never reach a log line, a browser URL, or a credential lookup key.
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        // The port is not part of a credential's identity as far as this is used, and it is
        // never part of a host name shown to a person. Kept separately rather than discarded:
        // a link to a self-hosted forge needs it back. See Port.
        var port = authority.IndexOf(':');
        var host = port < 0 ? authority : authority[..port];
        var portNumber = port < 0 ? string.Empty : authority[(port + 1)..];

        var transport = protocol switch
        {
            "http" or "https" => RemoteTransport.Http,
            "ssh" or "git+ssh" => RemoteTransport.Ssh,
            "file" => RemoteTransport.Local,
            _ => RemoteTransport.Unknown,
        };

        var (owner, repository) = SplitPath(path);

        return new RemoteUrl
        {
            Raw = raw,
            Transport = transport,
            Protocol = transport == RemoteTransport.Local ? string.Empty : protocol,
            Host = host,
            Port = portNumber,
            Owner = owner,
            Repository = repository,
        };
    }

    private static (string Owner, string Repository) SplitPath(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0
            ? (string.Empty, StripGitSuffix(trimmed))
            : (trimmed[..lastSlash], StripGitSuffix(trimmed[(lastSlash + 1)..]));
    }

    private static string StripGitSuffix(string name) =>
        name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// The folder name git would derive from a URL when cloning it.
    /// </summary>
    /// <remarks>
    /// Handles the scp-like form, whose last separator is a colon rather than a slash, and both
    /// directory separators — a Windows path is a perfectly good remote and is how the clone
    /// path is tested.
    /// </remarks>
    public static string RepositoryName(string url)
    {
        var trimmed = (url ?? string.Empty).TrimEnd('/', '\\', ' ');
        var cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var last = cut < 0 ? trimmed : trimmed[(cut + 1)..];

        return StripGitSuffix(last);
    }
}
