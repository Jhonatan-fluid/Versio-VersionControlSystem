namespace Versio.Web;

/// <summary>
/// Everything the web viewer is configured with: the <c>Web</c> section of configuration,
/// overridable by environment variables (<c>Web__GitHub__ClientId</c>) as ASP.NET does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Secrets are configuration, never code or a committed file.</b> The client secret and the
/// App's private key come from the host's secret store through environment variables
/// (docs/web/PLAN.md, "Access to GitHub" item 5). Nothing here is ever written to the log.
/// </para>
/// <para>
/// <b>With no GitHub App configured the viewer still works, anonymously, on public
/// repositories</b>, through GitHub's own unauthenticated limit. That is what a local run sees
/// before anyone registers an App, and it is never a way into a private repository: GitHub
/// answers those with 404 to an anonymous caller.
/// </para>
/// </remarks>
public sealed class WebOptions
{
    public const string Section = "Web";

    /// <summary>Kill switch: false refuses every request that is not signed in (security rule 17).</summary>
    public bool AnonymousAccess { get; set; } = true;

    /// <summary>
    /// The repositories this server shows, each as <c>owner/repo</c>. Empty: any repository the
    /// caller's credential can read — the open mode a public deployment runs in. Set, it is the
    /// server's whole scope (PLAN.md, <i>The product shape</i>): everything else answers "not
    /// found" whoever asks, and <c>/api/repos</c> lists these, filtered to what the caller can
    /// actually read. Only the host's configuration writes this; there is deliberately no API
    /// that does, because the server keeps no writable state.
    /// </summary>
    public string[] Repositories { get; set; } = [];

    /// <summary>Whether this server shows <paramref name="owner"/>/<paramref name="repo"/> at all.</summary>
    /// <remarks>Case-insensitive, as GitHub's own names are.</remarks>
    public bool Shows(string owner, string repo) =>
        Repositories.Length == 0
        || Repositories.Any(entry => string.Equals(entry, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Kill switch: bumping this ends every session at once, because a session records the epoch
    /// it was created under and is refused when they differ (security rule 17).
    /// </summary>
    public int SessionEpoch { get; set; }

    /// <summary>How long one render may take before the file is shown as plain text (security rule 3).</summary>
    public TimeSpan RenderBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The largest file fetched at all. Larger ones are named, never downloaded.</summary>
    public long MaxBlobBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>The largest file parsed and drawn; up to <see cref="MaxBlobBytes"/> it is shown as text.</summary>
    public long MaxDrawBytes { get; set; } = 2L * 1024 * 1024;

    /// <summary>The most changed files one comparison lists; the rest are counted, not listed (security rule 8).</summary>
    public int MaxComparedFiles { get; set; } = 2000;

    /// <summary>A session idle longer than this is ended (security rule 7).</summary>
    public TimeSpan SessionIdle { get; set; } = TimeSpan.FromDays(14);

    /// <summary>A session older than this is ended however active (security rule 7).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Requests per minute per client address, for everyone (security rule 8).</summary>
    public int RequestsPerMinute { get; set; } = 240;

    /// <summary>
    /// The reverse proxies whose X-Forwarded-For and X-Forwarded-Proto are believed: addresses, or
    /// networks as CIDR (<c>10.0.0.0/8</c>). Empty: none is, and the client is the connection.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Where the daily log file goes. Empty: the desktop's own log folder.</summary>
    public string LogDirectory { get; set; } = string.Empty;

    public GitHubOptions GitHub { get; set; } = new();
}

/// <summary>The GitHub App and the hosts the viewer may talk to.</summary>
public sealed class GitHubOptions
{
    /// <summary>The REST API. GitHub Enterprise Server: <c>https://host/api/v3</c>. Configuration only, never user input.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// The GraphQL endpoint, which review threads, their resolved and outdated state and the
    /// review decision are only available from. Empty: <see cref="ApiBaseUrl"/> + <c>/graphql</c>,
    /// right for github.com; GitHub Enterprise Server's is <c>https://host/api/graphql</c>, beside
    /// rather than under its REST base, so it is set there. Configuration only, never user input.
    /// </summary>
    public string GraphQlUrl { get; set; } = string.Empty;

    /// <summary>Where sign-in and LFS live. GitHub Enterprise Server: <c>https://host</c>.</summary>
    public string WebBaseUrl { get; set; } = "https://github.com";

    /// <summary>The viewer's own public address, for the OAuth callback. Empty: the request's.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The App's numeric id, for the JWT that mints installation tokens.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>The App's private key, PEM. Signs the JWT; never logged, never sent anywhere.</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>The App's URL name, for the "install it on this repository" link.</summary>
    public string AppSlug { get; set; } = string.Empty;

    /// <summary>
    /// Hosts an LFS download may be fetched from. Anything else is refused, whatever the LFS
    /// server answered (security rule 4).
    /// </summary>
    public List<string> LfsHosts { get; set; } =
    [
        "github-cloud.githubusercontent.com",
        "github-cloud.s3.amazonaws.com",
        "media.githubusercontent.com",
        "objects.githubusercontent.com",
    ];

    /// <summary>Sign-in is offered only when the App is configured.</summary>
    public bool SignInConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;

    /// <summary>Installation tokens are minted only when the App's key is configured.</summary>
    public bool AppConfigured => AppId.Length > 0 && PrivateKeyPem.Length > 0;
}
