using System.Text.RegularExpressions;

namespace Versio.Core.Codesys;

public sealed record CodesysInstall
{
    /// <summary>Directory name, e.g. "CODESYS 3.5.22.20".</summary>
    public required string InstallFolder { get; init; }

    /// <summary>Full path to CODESYS.exe.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Profile names found for this install, newest first.</summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];

    public string? FileVersion { get; init; }
}

public enum ProfileResolution
{
    /// <summary>Exactly one profile — use it.</summary>
    Resolved,

    /// <summary>Several profiles; the user must choose. Never guess.</summary>
    AmbiguousNeedsUserChoice,

    /// <summary>An install exists but no profile could be found.</summary>
    NoProfileFound,

    /// <summary>No CODESYS install at all.</summary>
    NoInstallFound,
}

public readonly record struct ProfileResolutionResult(
    ProfileResolution Outcome,
    string? Profile,
    IReadOnlyList<string> Candidates,
    string Explanation);

/// <summary>
/// Works out the <c>--profile</c> string CODESYS needs.
/// </summary>
/// <remarks>
/// <para>
/// This is not a convenience: <b>CODESYS refuses to run in <c>--noUI</c> mode without an
/// explicit profile</b>, exiting 1 with "When running in --noUI mode, you must specify a
/// profile using --profile=". So resolving it is a hard precondition for Versio doing
/// anything at all, and belongs in the startup gate rather than in the export path.
/// </para>
/// <para>
/// <b>The profile name cannot be derived from the install folder.</b> Measured on this
/// machine: the folder is <c>CODESYS 3.5.22.20</c> but the profile is
/// <c>CODESYS V3.5 SP22 Patch 2</c> — "22.20" is SP22 Patch 2, not Patch 20. Any attempt
/// to synthesise the string from the version number produces a name that does not exist.
/// It must be read from disk.
/// </para>
/// </remarks>
public static partial class ProfileResolver
{
    /// <summary>The exact refusal CODESYS emits when <c>--profile</c> is missing.</summary>
    public const string MissingProfileMessage =
        "When running in --noUI mode, you must specify a profile using --profile=";

    [GeneratedRegex(@"^(?<name>.+?)\.profile(\.xml)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFileName();

    [GeneratedRegex(@"SP(?<sp>\d+)(?:\s+Patch\s+(?<patch>\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileVersion();

    /// <summary>Turn a profile file name into the string CODESYS expects on the command line.</summary>
    /// <remarks>
    /// Active profiles are <c>&lt;name&gt;.profile.xml</c>; the <c>Informational</c>
    /// subfolder holds historical <c>&lt;name&gt;.profile</c> files for older versions,
    /// which are <b>not</b> usable and must be excluded by the caller.
    /// </remarks>
    public static string? ProfileNameFromFile(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var m = ProfileFileName().Match(name);
        return m.Success ? m.Groups["name"].Value : null;
    }

    /// <summary>Rank newest first, so a machine with several profiles offers the most
    /// recent one at the top of the picker.</summary>
    public static IReadOnlyList<string> RankNewestFirst(IEnumerable<string> profiles) =>
        profiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(SortKey)
            .ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static (int Sp, int Patch) SortKey(string profile)
    {
        var m = ProfileVersion().Match(profile);
        if (!m.Success)
        {
            return (0, 0);
        }

        var sp = int.TryParse(m.Groups["sp"].Value, out var s) ? s : 0;
        var patch = m.Groups["patch"].Success && int.TryParse(m.Groups["patch"].Value, out var p)
            ? p
            : 0;
        return (sp, patch);
    }

    /// <summary>
    /// Decide which profile to use. A pinned value always wins; otherwise a single
    /// candidate is accepted and several are escalated to the user.
    /// </summary>
    public static ProfileResolutionResult Resolve(
        IReadOnlyList<CodesysInstall> installs, string? pinnedProfile = null)
    {
        if (installs.Count == 0)
        {
            return new ProfileResolutionResult(ProfileResolution.NoInstallFound, null, [],
                "no CODESYS installation found; Versio cannot export or import without one");
        }

        var all = RankNewestFirst(installs.SelectMany(i => i.Profiles));

        if (!string.IsNullOrWhiteSpace(pinnedProfile))
        {
            // A pinned profile that no longer exists must fail loudly rather than silently
            // falling back — running a project against the wrong service pack can trigger a
            // silent project upgrade.
            return all.Contains(pinnedProfile, StringComparer.OrdinalIgnoreCase)
                ? new ProfileResolutionResult(ProfileResolution.Resolved, pinnedProfile, all,
                    "using the profile pinned for this project")
                : new ProfileResolutionResult(ProfileResolution.NoProfileFound, null, all,
                    $"this project is pinned to profile '{pinnedProfile}', which is not installed. " +
                    $"Installed: {(all.Count == 0 ? "none" : string.Join(", ", all))}");
        }

        return all.Count switch
        {
            0 => new ProfileResolutionResult(ProfileResolution.NoProfileFound, null, [],
                "a CODESYS installation was found but it has no usable profile"),
            1 => new ProfileResolutionResult(ProfileResolution.Resolved, all[0], all,
                "exactly one profile installed"),
            _ => new ProfileResolutionResult(ProfileResolution.AmbiguousNeedsUserChoice, null, all,
                $"{all.Count} profiles installed; pick the one this project targets"),
        };
    }

    /// <summary>Recognise CODESYS's refusal so it becomes an actionable message instead of
    /// a generic non-zero exit.</summary>
    public static bool IsMissingProfileError(string? stdoutOrStderr) =>
        stdoutOrStderr is not null &&
        stdoutOrStderr.Contains(MissingProfileMessage, StringComparison.OrdinalIgnoreCase);
}
