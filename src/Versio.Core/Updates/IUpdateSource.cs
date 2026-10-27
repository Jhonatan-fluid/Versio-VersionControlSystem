namespace Versio.Core.Updates;

/// <summary>
/// Where newer versions of Versio come from, and how one is installed.
/// </summary>
/// <remarks>
/// <para>
/// In Core as an interface so that the rules about <i>when</i> to ask, what a dismissal means
/// and when it is safe to restart are tested without GitHub, without an installed copy and
/// without a network. The real one wraps Velopack in the app; tests hand in a fake.
/// </para>
/// <para>
/// <b>Installing never happens while Versio runs.</b> <see cref="ApplyAfterExit"/> hands the
/// downloaded package to an updater that waits for this process to end. The decision about
/// whether Versio may end yet - an import halfway through a project file - stays with the
/// close guard, which is the one place that already knows how to make it.
/// </para>
/// </remarks>
public interface IUpdateSource
{
    /// <summary>The version this process is, as the installer numbered it.</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Why this copy cannot update itself, or null when it can.
    /// </summary>
    /// <remarks>
    /// A development build run from <c>bin\</c> is the common case: there is no installation
    /// for an update to replace. Stated rather than reported as a failure, because it is not
    /// one - it is the expected answer for every F5.
    /// </remarks>
    string? Unavailable { get; }

    /// <summary>Ask whether a newer version has been published.</summary>
    Task<UpdateCheck> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Fetch the offered version into the local package store, reporting 0-100.
    /// </summary>
    /// <remarks>
    /// Downloads only. Nothing installed changes, so cancelling or failing here costs a
    /// re-download and nothing else.
    /// </remarks>
    Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct);

    /// <summary>Whether a downloaded update is waiting to be installed.</summary>
    bool HasDownloadedUpdate { get; }

    /// <summary>
    /// Hand the downloaded update to the updater, which installs it once this process exits.
    /// </summary>
    /// <remarks>
    /// Called from the application's exit, after the close guard has agreed. The updater
    /// waits for the process to end before touching a file, so everything still to run in
    /// the exit - releasing the repository lock, flushing the log - finishes first.
    /// </remarks>
    /// <param name="restart">Start Versio again afterwards.</param>
    void ApplyAfterExit(bool restart);
}

/// <summary>A published version newer than this one.</summary>
/// <param name="Version">The version as published, e.g. <c>0.5.0</c>.</param>
/// <param name="NotesMarkdown">The release notes the publisher wrote, or empty.</param>
public sealed record UpdateOffer(string Version, string NotesMarkdown);

/// <summary>What a check found.</summary>
public enum UpdateCheckResult
{
    /// <summary>Nothing newer is published.</summary>
    UpToDate,

    /// <summary>A newer version is published; <see cref="UpdateCheck.Offer"/> says which.</summary>
    Available,

    /// <summary>This copy cannot update itself - a development build, say.</summary>
    Unavailable,

    /// <summary>The check could not be completed; <see cref="UpdateCheck.Reason"/> says why.</summary>
    Failed,
}

/// <summary>The outcome of one check, with the reason when there is no offer.</summary>
public sealed record UpdateCheck(UpdateCheckResult Result, UpdateOffer? Offer, string Reason)
{
    public static UpdateCheck UpToDate() => new(UpdateCheckResult.UpToDate, null, string.Empty);

    public static UpdateCheck Available(UpdateOffer offer) =>
        new(UpdateCheckResult.Available, offer, string.Empty);

    public static UpdateCheck Unavailable(string reason) =>
        new(UpdateCheckResult.Unavailable, null, reason);

    public static UpdateCheck Failed(string reason) => new(UpdateCheckResult.Failed, null, reason);
}

/// <summary>
/// The update source of a process that cannot update: every check says so.
/// </summary>
/// <remarks>
/// The default a shell gets, for the same reason it gets a settings store that remembers
/// nothing: constructing one in a test must never reach out to GitHub, and a host that forgot
/// to supply a source gets a Versio that does not update rather than one that guesses.
/// </remarks>
public sealed class NullUpdateSource : IUpdateSource
{
    public static readonly NullUpdateSource Instance = new();

    private NullUpdateSource()
    {
    }

    public string CurrentVersion => string.Empty;

    public string? Unavailable => "updates are not configured for this process";

    public Task<UpdateCheck> CheckAsync(CancellationToken ct) =>
        Task.FromResult(UpdateCheck.Unavailable(Unavailable!));

    public Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct) =>
        Task.CompletedTask;

    public bool HasDownloadedUpdate => false;

    public void ApplyAfterExit(bool restart)
    {
    }
}
