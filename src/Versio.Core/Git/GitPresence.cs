namespace Versio.Core.Git;

/// <summary>Whether the <c>git</c> Versio drives everything through is actually there.</summary>
public enum GitAvailability
{
    /// <summary>Nobody has asked yet. Distinct from <see cref="Missing"/> on purpose — see
    /// <see cref="GitPresence.Unknown"/>.</summary>
    Unknown,

    /// <summary>It ran and said what version it is.</summary>
    Present,

    /// <summary>It could not be run at all.</summary>
    Missing,
}

/// <summary>
/// The result of asking <c>git</c> whether it exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versio has no git of its own.</b> Every operation in the application is
/// <c>git.exe</c> resolved from <c>PATH</c>, which is a deliberate choice — it is what makes
/// Git Credential Manager, SSH agents, 2FA and corporate proxies behave exactly as they do in
/// the engineer's own terminal. The cost of that choice is this type: on a machine without git,
/// nothing works, and the application has to say so in a sentence rather than by failing.
/// </para>
/// <para>
/// <b>Asked once at launch, not at first use.</b> Before this existed the only caller was the
/// System Info window, which answered <i>"not checked — open a repository"</i> when none was
/// open — so the one question a git-less machine needs answered was the one question that could
/// not be asked. Everything else surfaced the absence as a <see cref="System.ComponentModel.Win32Exception"/>
/// from somewhere deep, which reads as a bug in Versio rather than as a missing dependency.
/// </para>
/// <para>
/// <b><see cref="GitAvailability.Unknown"/> is not <see cref="GitAvailability.Missing"/>.</b>
/// Collapsing them would make the application announce that git is missing for the moment
/// between the window opening and the probe returning, which is a lie that flashes on every
/// launch. Callers gate on <see cref="IsUsable"/> and say nothing until they have an answer.
/// </para>
/// </remarks>
public sealed record GitPresence
{
    /// <summary>Nothing has been probed yet.</summary>
    public static readonly GitPresence Unknown = new() { Availability = GitAvailability.Unknown };

    public required GitAvailability Availability { get; init; }

    /// <summary>Git's own version line, verbatim, or empty when it did not run.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Where the <c>git</c> that will actually run lives.
    /// </summary>
    /// <remarks>
    /// Recorded because "which git is it using" has caused real confusion on machines carrying
    /// several — a Git for Windows install, one inside a vendor toolchain, and one on a build
    /// agent's PATH all answer <c>--version</c> and only one of them is the one with the
    /// credentials. Empty when it could not be resolved, which is not itself a failure.
    /// </remarks>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>Why it could not be run. Empty unless <see cref="Availability"/> is
    /// <see cref="GitAvailability.Missing"/>.</summary>
    public string FailureReason { get; init; } = string.Empty;

    /// <summary>True when git ran. Everything that touches a repository gates on this.</summary>
    public bool IsUsable => Availability == GitAvailability.Present;

    /// <summary>
    /// One sentence for the panel, whichever state this is in.
    /// </summary>
    /// <remarks>
    /// The missing case names <c>PATH</c> explicitly. "Git is not installed" is wrong about
    /// half the time — the usual cause is a git that exists but is not on the PATH of the
    /// process Versio was launched from, and telling somebody to install what they already have
    /// sends them the wrong way for an afternoon.
    /// </remarks>
    public string Explanation => Availability switch
    {
        GitAvailability.Present => Version,
        GitAvailability.Missing =>
            "Git could not be run. Versio drives the real git for everything it does, so " +
            "nothing in this panel can work without it. Check that git is installed and on " +
            "this machine's PATH." +
            (FailureReason.Length == 0 ? string.Empty : $" ({FailureReason})"),
        _ => "Checking for git…",
    };

    /// <summary>Git ran and said this.</summary>
    /// <param name="version">Git's own <c>--version</c> line.</param>
    /// <param name="executablePath">Where it was resolved from, or empty if unknown.</param>
    public static GitPresence Found(string version, string executablePath) => new()
    {
        Availability = GitAvailability.Present,
        Version = version.Trim(),
        ExecutablePath = executablePath.Trim(),
    };

    /// <summary>Git could not be run, for this reason.</summary>
    public static GitPresence NotFound(string reason) => new()
    {
        Availability = GitAvailability.Missing,
        FailureReason = reason.Trim(),
    };
}
