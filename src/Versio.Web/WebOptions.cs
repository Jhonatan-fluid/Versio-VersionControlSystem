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

    /// <summary>A session idle longer than this is ended (security rule 7).</summary>
    public TimeSpan SessionIdle { get; set; } = TimeSpan.FromDays(14);

    /// <summary>A session older than this is ended however active (security rule 7).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Requests per minute per client address, for everyone (security rule 8).</summary>
    public int RequestsPerMinute { get; set; } = 240;

    /// <summary>Where the daily log file goes. Empty: the desktop's own log folder.</summary>
    public string LogDirectory { get; set; } = string.Empty;

    public GitHubOptions GitHub { get; set; } = new();
}

/// <summary>The GitHub App and the hosts the viewer may talk to.</summary>
public sealed class GitHubOptions
{
    /// <summary>The REST API. GitHub Enterprise Server: <c>https://host/api/v3</c>. Configuration only, never user input.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

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
