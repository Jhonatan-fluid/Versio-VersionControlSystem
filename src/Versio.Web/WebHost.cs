using System.Net;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Logging;
using Versio.Infrastructure.Platform;
using Versio.Plc.Codesys;
using Versio.Plc.TiaPortal;
using Versio.Plc.TwinCat;
using Versio.Web.Api;
using Versio.Web.Auth;
using Versio.Web.Http;
using Versio.Web.Rendering;
using Versio.Web.Repo;
using Versio.Web.Review;
using Versio.Web.Sources;

namespace Versio.Web;

/// <summary>The web viewer's composition root: services, middleware, routes.</summary>
/// <remarks>
/// <para>
/// <b>Vendors appear here and nowhere else in this project</b>: one <c>PlcFormats.Register</c>
/// per pure pack, the web counterpart of <c>CodesysPack.Register()</c> in the desktop's
/// <c>App.xaml.cs</c>. Those calls live in the packs' Infrastructure halves, which also register
/// toolchains that drive an IDE, and this server may reference none of that. A format is all a
/// viewer needs: it is the part that parses a vendor's files.
/// </para>
/// <para>
/// <b>The regex timeout is set before anything else runs</b> (security rule 3). It is a process
/// default read once, the first time the runtime touches <see cref="Regex"/>, so it has to be
/// set before any pattern is built, by any code. <see cref="RegexTimeoutApplied"/> reports
/// whether it took, and startup says so in the log.
/// </para>
/// </remarks>
public static class WebHost
{
    public const string RatePolicy = "per-client";

    public const string WritePolicy = "per-session-writes";

    /// <summary>The longest any one regular expression match may run, anywhere in the process.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Process-wide settings that must precede everything: today, the regex timeout.</summary>
    public static void ConfigureProcess() => AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", RegexTimeout);

    /// <summary>Whether a pattern built without its own timeout gets <see cref="RegexTimeout"/>.</summary>
    public static bool RegexTimeoutApplied => new Regex("x", RegexOptions.None).MatchTimeout == RegexTimeout;

    /// <summary>The vendor formats this server parses with.</summary>
    public static void RegisterFormats()
    {
        PlcFormats.Register(CodesysProjectFormat.Instance);
        PlcFormats.Register(TiaPortalProjectFormat.Instance);
        PlcFormats.Register(TwinCatProjectFormat.Instance);
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        ConfigureProcess();
        RegisterFormats();

        // The content root is the server's own folder, not wherever it was started from: the
        // built page (wwwroot) and appsettings.json are copied beside the assembly, and a
        // server started from the repository root would otherwise serve no page at all.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        var section = builder.Configuration.GetSection(WebOptions.Section);
        builder.Services.Configure<WebOptions>(section);
        var options = section.Get<WebOptions>() ?? new WebOptions();

        var logger = new VersioLogger(new SystemClock());
        logger.AddSink(new ConsoleLogSink());
        var logDirectory = options.LogDirectory.Length > 0
            ? options.LogDirectory
            // A folder of its own: the desktop writes versio-<date>.log in the parent, and two
            // processes appending to one file would interleave their lines.
            : Path.Combine(RollingFileSink.DefaultDirectory, "web");
        var file = new RollingFileSink(logDirectory);
        logger.AddSink(file);
        builder.Services.AddSingleton<IVersioLogger>(logger);
        builder.Services.AddSingleton(file);

        builder.Services.AddSingleton<IClock, SystemClock>();

        // Size units are roughly kilobytes (each entry says how many it is), so this is about
        // 256 MB of blobs, fragments and models before the oldest are evicted.
        builder.Services.AddMemoryCache(o => o.SizeLimit = 256 * 1024);

        builder.Services.AddHttpClient(GitHubRepoSource.ApiClient, c => c.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHttpClient(GitHubRepoSource.LfsClient, c => c.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(LfsGuard.CreateHandler);

        builder.Services.AddSingleton<IRepoSource, GitHubRepoSource>();
        // Sessions live in a distributed cache: Redis when Web:Redis is set, so every instance
        // sees every session; in memory otherwise, for one instance. The rendered-content cache
        // stays per instance (AddMemoryCache above): it is keyed by content, so each instance
        // computes the same answer and nothing about it needs to be shared.
        var redis = builder.Configuration["Web:Redis"];
        if (!string.IsNullOrEmpty(redis))
        {
            builder.Services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "versio-web:";
            });
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<GitHubOAuth>();
        // The App key signs here today; a key-vault signer replaces this line (IAppSigner, rule 15).
        builder.Services.AddSingleton<IAppSigner, PemAppSigner>();
        builder.Services.AddSingleton<IInstallationTokens, AppTokens>();
        builder.Services.AddSingleton<AccessResolver>();
        builder.Services.AddSingleton<RepoModelLoader>();
        builder.Services.AddSingleton<ViewService>();
        builder.Services.AddSingleton<ApiEndpoints.Loader>();
        builder.Services.AddSingleton<ReviewSnapshots>();
        builder.Services.AddSingleton<PullReview>();
        builder.Services.AddSingleton<IRepoWriter, GitHubWriter>();
        builder.Services.AddSingleton<PullWriteEndpoints.Writes>();

        // The operation ledger (C5): durable and shared through Redis when it is configured, in
        // memory only when the host has declared a single instance. ValidateWrites refuses the
        // combination that would silently lose reservations across instances.
        if (!string.IsNullOrEmpty(redis))
        {
            builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ => StackExchange.Redis.ConnectionMultiplexer.Connect(redis));
            builder.Services.AddSingleton<IOperationLedger, RedisOperationLedger>();
        }
        else
        {
            builder.Services.AddSingleton<IOperationLedger, MemoryOperationLedger>();
        }

        var keys = builder.Configuration["Web:KeysDirectory"];
        var protection = builder.Services.AddDataProtection().SetApplicationName("Versio.Web");
        if (!string.IsNullOrEmpty(keys))
        {
            // Shared by every instance, or a cookie one instance sealed is unreadable to the next
            // and people are signed out at random (docs/web/PLAN.md Step 2). Production protects
            // the ring itself with the host's key vault (security rule 7).
            protection.PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        // Responses compressed. Measured on 2026-10-11: the 33,644-line visualisation's Raw view is
        // 7.5 MB of HTML, and a comparison of it 18 MB; the markup repeats per line, so it shrinks
        // by an order of magnitude. Safe over HTTPS here (the BREACH concern) because no response
        // body carries a secret: tokens stay server-side and logout's guard is a header, not a
        // token in a page.
        builder.Services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
            o.MimeTypes = ["application/json", "text/html", "text/css", "text/javascript", "application/javascript", "image/svg+xml"];
        });
        builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // The limiter answers before any endpoint runs: THIS request did nothing, said in this
            // server's own JSON, which the page reads as a refusal of this request. It says nothing
            // about an earlier attempt under the same key, so it claims nothing about one either (the
            // first wording said "nothing was posted", which is untrue on a retry; found by review,
            // 2026-10-20), and it carries no settled mark, so a held key is kept.
            o.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate-limited", message = "Too many requests: this one was not acted on. Wait a minute, then try again." }, ct).ConfigureAwait(false);
            };
            // Writes per signed-in session (S8, and rule 8's per-user half): a session is one person,
            // so one person cannot spend their GitHub limit through us or flood a pull request. It
            // REPLACES the per-address policy on write routes — an endpoint's own policy wins over its
            // group's — which is the right bound there: WriteGate refuses everyone without a session
            // before the limiter runs, so only sessions ever reach it (an earlier comment here claimed
            // both applied; caught on the second pass).
            o.AddPolicy(WritePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                "session:" + (http.Request.Cookies[AccessResolver.SessionCookie] ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.Writes.PerSessionPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            o.AddPolicy(RatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.RequestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        var forwarded = ForwardedFrom(options.TrustedProxies);
        ValidateRepositories(options.Repositories);
        ValidateWrites(options, redis);

        configure?.Invoke(builder);

        var app = builder.Build();

        // First, so the rate limiter, the request log and the OAuth callback's scheme all see the
        // client rather than the proxy. Behind a load balancer every request arrives from the
        // balancer's address, so the first version's per-address limit was one bucket for the
        // whole internet: one busy client refused everyone (found in review). Not at all without a
        // trusted proxy: see ForwardedFrom.
        if (forwarded is not null)
        {
            app.UseForwardedHeaders(forwarded);
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeaders>();
        app.UseMiddleware<RequestLog>();

        // Static files BEFORE routing, explicitly. The static-file middleware stands aside when
        // routing has already chosen an endpoint, and the page's fallbacks below match
        // /assets/index-*.js too, so with routing first the page's own script and stylesheet
        // were 404s (seen in the browser on 2026-10-11, the moment the fallbacks were added).
        app.UseResponseCompression();
        app.UseDefaultFiles();
        var staticFiles = new StaticFileOptions
        {
            // Vite names every built asset by its content hash (index-PYcC7phc.js), so a name
            // never points at different bytes: cache it for a year. index.html is the one file
            // whose name stays while its content changes, so it is always revalidated.
            //
            // Only Vite's hashed output under /assets is immutable. Every page and every other file
            // keeps its name across deploys, so it is revalidated: the first rule gave those an
            // hour, and a browser kept a stale mermaid-frame.html (seen in the browser, 2026-10-11).
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                var hashed = context.Context.Request.Path.StartsWithSegments("/assets")
                             && !context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
                headers.CacheControl = hashed ? "public, max-age=31536000, immutable" : "no-cache";
            },
        };
        app.UseStaticFiles(staticFiles);
        app.UseRouting();
        // Before the rate limiter: a forged or switched-off write is refused without spending
        // anybody's write budget, and before any endpoint reads a body (PullWriteEndpoints.WriteGate).
        app.UseMiddleware<PullWriteEndpoints.WriteGate>();
        app.UseRateLimiter();

        ApiEndpoints.Map(app);
        PullEndpoints.Map(app);
        PullWriteEndpoints.Map(app);
        AuthEndpoints.Map(app);

        // An unknown API route is a 404, never the page's index.html with a 200.
        app.MapFallback("/api/{**rest}", () => ApiEndpoints.NotFoundJson());
        app.MapFallback("/auth/{**rest}", () => Results.NotFound());
        app.MapFallback("/assets/{**rest}", () => Results.NotFound());

        // The page's own URLs end in the file they show (/acme/plant/blob/main/Signals.xml), and
        // the default fallback skips any path that looks like a file, so a pasted or reloaded
        // link to almost every PLC file was a 404. Seen in the browser on 2026-10-11; every view
        // having its own URL is the point of the page (docs/web/PLAN.md). Real static files are
        // served before routing, so these never shadow one.
        // The same options, so index.html served at a deep link is revalidated too: a cached old
        // page after a deploy would ask for script names that no longer exist (found measuring).
        app.MapFallbackToFile("/{owner}/{repo}/{**rest}", "index.html", staticFiles);
        app.MapFallbackToFile("index.html", staticFiles);

        if (!string.IsNullOrEmpty(redis) && string.IsNullOrEmpty(keys))
        {
            // Shared sessions, private keys: each instance, and each restart, has its own key
            // ring, so a session another instance sealed cannot be decrypted here. SessionStore
            // treats that as signed out rather than failing, but everyone is still signed out at
            // random. Said once, loudly, at startup (found in review).
            logger.Warn(LogCategory.App,
                "Web:Redis is set but Web:KeysDirectory is not: sessions are shared, the keys that seal them are not, so people will be signed out at random; give every instance one key ring");
        }

        var g = options.GitHub;
        // The regex state is IN the starting line, not a separate warning after it: a reader (or a
        // test) that stops at "starting" must still see it. The first version logged it after,
        // and the startup test stopped reading before it ever arrived.
        var regex = RegexTimeoutApplied
            ? $"regex timeout {RegexTimeout.TotalSeconds:0} s in force"
            : "regex timeout NOT in force: a pattern ran before it was set (security rule 3)";
        logger.Log(RegexTimeoutApplied ? Versio.Core.Logging.LogLevel.Information : Versio.Core.Logging.LogLevel.Warning, LogCategory.App,
            $"Versio Web starting: {regex}, sign-in {(g.SignInConfigured ? "configured" : "not configured (anonymous, public repositories only)")}, "
            + $"installation tokens {(g.AppConfigured ? "configured" : "not configured")}, anonymous access {(options.AnonymousAccess ? "on" : "off")}, "
            + $"API {g.ApiBaseUrl}, keys {(string.IsNullOrEmpty(keys) ? "in memory (sessions end on restart)" : "persisted")}, "
            + $"trusted proxies {(options.TrustedProxies.Length == 0 ? "none" : string.Join(' ', options.TrustedProxies))}, "
            + $"repositories {(options.Repositories.Length == 0 ? "any the credential can read" : $"{options.Repositories.Length} listed")}, log {file.LogDirectory}");

        return app;
    }

    /// <summary>
    /// Forwarded headers believed from <paramref name="proxies"/> only; null when none is
    /// configured, and then the middleware must not run at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null, not empty lists.</b> ASP.NET's middleware treats empty known-proxy and
    /// known-network lists as "trust every sender", not "trust none". The first version cleared
    /// both and ran it anyway, and the test that sends X-Forwarded-For from an untrusted client
    /// got a fresh rate bucket per request. A header anyone can send must never choose whose
    /// bucket a request lands in.
    /// </para>
    /// <para>
    /// No forward limit: the middleware walks back through X-Forwarded-For only while the hop it
    /// came from is a trusted proxy, so a chain of them (a CDN, then a balancer) resolves to the
    /// client, and an address the client wrote itself is never reached. A malformed entry stops
    /// startup rather than trusting less, or more, than was meant.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Refuses a malformed <c>Web:Repositories</c> entry at startup, the way a malformed trusted
    /// proxy is refused: a scope that silently dropped an entry would show less, or more, than the
    /// admin meant, and nobody would see it until somebody could not open a repository.
    /// </summary>
    internal static void ValidateRepositories(IReadOnlyList<string> repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        foreach (var entry in repositories)
        {
            var slash = entry.IndexOf('/');
            if (slash <= 0 || !Api.ApiEndpoints.Valid(entry[..slash], entry[(slash + 1)..]))
            {
                throw new InvalidOperationException($"Web:Repositories: '{entry}' is not owner/repo.");
            }
        }
    }

    /// <summary>
    /// Refuses to start with writes configured unsafely: a malformed entry, a writable repository the
    /// server does not even show, or writes on more than one possible instance without a shared
    /// ledger (C5) — the last would let two instances each reserve the same operation and post twice.
    /// </summary>
    internal static void ValidateWrites(WebOptions options, string? redis)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var entry in options.Writes.Repositories)
        {
            var slash = entry.IndexOf('/');
            if (slash <= 0 || !Api.ApiEndpoints.Valid(entry[..slash], entry[(slash + 1)..]))
            {
                throw new InvalidOperationException($"Web:Writes:Repositories: '{entry}' is not owner/repo.");
            }

            if (!options.Shows(entry[..slash], entry[(slash + 1)..]))
            {
                throw new InvalidOperationException($"Web:Writes:Repositories: '{entry}' is not in Web:Repositories, so the server would never show it.");
            }
        }

        if (options.Writes.Enabled && string.IsNullOrEmpty(redis) && !options.Writes.SingleInstance)
        {
            throw new InvalidOperationException(
                "Web:Writes:Enabled needs Web:Redis (a shared, durable operation ledger), or Web:Writes:SingleInstance=true to declare that only one instance runs.");
        }
    }

    internal static ForwardedHeadersOptions? ForwardedFrom(IReadOnlyList<string> proxies)
    {
        if (proxies.Count == 0)
        {
            return null;
        }

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null,
        };
        forwarded.KnownNetworks.Clear();
        forwarded.KnownProxies.Clear();
        foreach (var proxy in proxies)
        {
            var cidr = proxy.Contains('/', StringComparison.Ordinal);
            if (cidr && Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(proxy, out var network))
            {
                forwarded.KnownNetworks.Add(network);
            }
            else if (!cidr && IPAddress.TryParse(proxy, out var address))
            {
                forwarded.KnownProxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException($"Web:TrustedProxies: '{proxy}' is neither an IP address nor a network in CIDR form.");
            }
        }

        return forwarded;
    }
}
