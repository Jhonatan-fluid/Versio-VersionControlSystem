using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
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

        var builder = WebApplication.CreateBuilder(args);
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
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<GitHubOAuth>();
        builder.Services.AddSingleton<IInstallationTokens, AppTokens>();
        builder.Services.AddSingleton<AccessResolver>();
        builder.Services.AddSingleton<RepoModelLoader>();
        builder.Services.AddSingleton<ViewService>();
        builder.Services.AddSingleton<ApiEndpoints.Loader>();

        var keys = builder.Configuration["Web:KeysDirectory"];
        var protection = builder.Services.AddDataProtection().SetApplicationName("Versio.Web");
        if (!string.IsNullOrEmpty(keys))
        {
            // Shared by every instance, or a cookie one instance sealed is unreadable to the next
            // and people are signed out at random (docs/web/PLAN.md Step 2). Production protects
            // the ring itself with the host's key vault (security rule 7).
            protection.PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.RequestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        configure?.Invoke(builder);

        var app = builder.Build();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeaders>();
        app.UseMiddleware<RequestLog>();
        app.UseRateLimiter();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        ApiEndpoints.Map(app);
        AuthEndpoints.Map(app);

        // An unknown API route is a 404, never the page's index.html with a 200.
        app.MapFallback("/api/{**rest}", () => Results.NotFound());
        app.MapFallback("/auth/{**rest}", () => Results.NotFound());
        app.MapFallbackToFile("index.html");

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
            + $"API {g.ApiBaseUrl}, keys {(string.IsNullOrEmpty(keys) ? "in memory (sessions end on restart)" : "persisted")}, log {file.LogDirectory}");

        return app;
    }
}
