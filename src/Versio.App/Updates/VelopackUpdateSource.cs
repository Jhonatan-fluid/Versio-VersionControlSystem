using Velopack;
using Velopack.Sources;
using Versio.Core.Updates;

namespace Versio.App.Updates;

/// <summary>
/// Updates from GitHub Releases, through Velopack - the installer Versio already ships with.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new UpdateManager per check, and that is because of the token.</b> Velopack takes the
/// GitHub token when the source is constructed, and the token is the user's stored git sign-in,
/// which can change between checks: signed out, signed in as somebody else, renewed. Building
/// the manager once at startup would pin whatever was stored at launch for the whole session.
/// The manager from the latest check is kept, because the download must use the same source
/// and the same release it found.
/// </para>
/// <para>
/// Velopack's own exceptions are turned into a <see cref="UpdateCheck"/> with a reason here,
/// so the view model deals in outcomes rather than in a library's exception types.
/// </para>
/// </remarks>
/// <param name="repositoryUrl">https://github.com/owner/name, or empty for "not configured".</param>
/// <param name="currentVersion">This build's version, as System information shows it.</param>
/// <param name="token">
/// The GitHub token to read the releases with, or null to go unauthenticated - which only a
/// public repository answers.
/// </param>
public sealed class VelopackUpdateSource(
    string repositoryUrl,
    string currentVersion,
    Func<CancellationToken, Task<string?>> token) : Versio.Core.Updates.IUpdateSource
{
    private UpdateManager? _manager;
    private UpdateInfo? _found;
    private bool _downloaded;

    public string CurrentVersion => currentVersion;

    public string? Unavailable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(repositoryUrl))
            {
                return "this build names no repository to update from";
            }

            // Not installed means run from bin\ - a development build, or a copy unzipped by
            // hand. There is no installation for an update to replace, and Velopack's check
            // would throw to say so.
            return IsInstalled() ? null : "this copy was not installed with Setup.exe (a development build)";
        }
    }

    public bool HasDownloadedUpdate => _downloaded;

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        if (Unavailable is { } why)
        {
            return UpdateCheck.Unavailable(why);
        }

        var secret = await token(ct).ConfigureAwait(false);

        try
        {
            var manager = new UpdateManager(new GithubSource(repositoryUrl, secret, prerelease: false));
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);

            _manager = manager;
            _found = info;

            if (info is null)
            {
                return UpdateCheck.UpToDate();
            }

            // Velopack fetches the delta chain when it has one and the full package when it
            // does not, so the size the overlay shows has to follow the same choice - the
            // full package's 80 MB beside a 3 MB delta download would be wrong by 25x.
            var target = info.TargetFullRelease;
            var bytes = info.DeltasToTarget is { Length: > 0 } deltas
                ? deltas.Sum(d => d.Size)
                : target.Size;
            return UpdateCheck.Available(
                new UpdateOffer(target.Version.ToString(), target.NotesMarkdown ?? string.Empty, bytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpdateCheck.Failed(Explain(ex, secret is not null));
        }
    }

    public async Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct)
    {
        if (_manager is null || _found is null
            || _found.TargetFullRelease.Version.ToString() != offer.Version)
        {
            throw new InvalidOperationException(
                $"no check has found {offer.Version}; check again before downloading");
        }

        await _manager.DownloadUpdatesAsync(_found, p => progress?.Report(p), ct)
            .ConfigureAwait(false);
        _downloaded = true;
    }

    public void ApplyAfterExit(bool restart)
    {
        if (_manager is null || _found is null || !_downloaded)
        {
            return;
        }

        // silent: false, so the updater shows its own progress window while it replaces the
        // files - otherwise the seconds between this window closing and the new one opening
        // look like a crash.
        _manager.WaitExitThenApplyUpdates(_found.TargetFullRelease, silent: false, restart: restart);
    }

    private static bool IsInstalled()
    {
        try
        {
            return new UpdateManager(new GithubSource("https://github.com/versio/none", null, false))
                .IsInstalled;
        }
        catch (Exception)
        {
            // Velopack's locator reads the install layout around the exe; a layout it cannot
            // make sense of is a copy that cannot update, which is what false means.
            return false;
        }
    }

    /// <summary>A sentence for the log and the toast, not a stack trace.</summary>
    /// <remarks>
    /// GitHub answers a private repository the caller cannot see with 404, not 403, so as not
    /// to confirm that it exists. Without a token that is the expected answer - and it reads
    /// like "the repository was deleted" unless somebody says otherwise.
    /// </remarks>
    private static string Explain(Exception ex, bool hadToken)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("404", StringComparison.Ordinal) || message.Contains("401", StringComparison.Ordinal)
                                                               || message.Contains("403", StringComparison.Ordinal))
        {
            return hadToken
                ? $"GitHub refused the stored sign-in for the releases repository ({message}). " +
                  "Your account may not have access to it yet."
                : "not signed in to GitHub, and the releases repository is private. " +
                  "Sign in from the Git panel, then check again.";
        }

        return message;
    }
}
