using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.Codesys;

namespace Versio.Plc.Codesys.Infrastructure;

/// <summary>
/// CODESYS 3.5, as something the application can ask for by vendor rather than by name.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPlcToolchain"/> was declared from the start and never implemented, so the
/// application assembled a toolchain out of vendor parts instead: it constructed a
/// <c>CodesysLocator</c>, called <c>ProfileResolver</c>, read a <c>CodesysInstall</c> and built
/// a <c>LocalCodesysRunner</c>. Five brand names in a view model that has no business knowing
/// any of them, and the last real leak once the core seam was closed.
/// </para>
/// <para>
/// Everything CODESYS-shaped about starting up now lives here: that an install has profiles,
/// that a profile name cannot be derived from a folder name, and that several installations
/// mean asking rather than guessing.
/// </para>
/// </remarks>
public sealed class CodesysToolchain : IPlcToolchain
{
    private readonly IVersioLogger _log;
    private readonly IEnumerable<string>? _searchRoots;

    public CodesysToolchain(IVersioLogger? logger = null)
        : this(logger, searchRoots: null)
    {
    }

    /// <summary>
    /// With the folders the locator searches, so a test can hand it an empty one and probe
    /// a machine with no CODESYS without needing one.
    /// </summary>
    internal CodesysToolchain(IVersioLogger? logger, IEnumerable<string>? searchRoots)
    {
        _log = logger ?? NullLogger.Instance;
        _searchRoots = searchRoots;
    }

    public IPlcProjectFormat Format => CodesysProjectFormat.Instance;

    /// <summary>
    /// Find the installation and settle on a profile.
    /// </summary>
    /// <remarks>
    /// The profile is not optional and cannot be guessed: CODESYS refuses to start headless
    /// without <c>--profile</c>, and the name does not follow from the install folder —
    /// <c>CODESYS 3.5.22.20</c> on disk is profile <c>CODESYS V3.5 SP22 Patch 2</c>. It has to
    /// be read from disk, and with several installed the user must choose rather than have
    /// Versio pick one and risk a silent project upgrade.
    /// </remarks>
    public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct)
    {
        var installs = new CodesysLocator(_searchRoots, logger: _log).FindInstalls();
        var resolution = ProfileResolver.Resolve(installs);

        // The install that OWNS the resolved profile, not simply the first one found.
        //
        // ProfileResolver flattens profiles across every install, so on a machine where the
        // first install has no usable profile and the second does, `installs[0]` reported the
        // second install's profile beside the FIRST install's executable and version - and
        // CreateRunner then launched that executable with a profile it may not have. Silent,
        // and exactly the kind of version mix-up the profile is resolved so carefully to avoid.
        //
        // Null when there is no install at all. This used to be `?? installs[0]`, which on a
        // machine with no CODESYS threw ArgumentOutOfRange before the resolver's NoInstallFound
        // below could be reported — and the probe runs inside the repository attach, so a
        // TIA-only machine aborted the attach and never probed the vendors after this one
        // (the 2026-10-24 review's item 36). Only the Resolved arm needs an owner.
        var owner = installs.FirstOrDefault(
            i => i.Profiles.Contains(resolution.Profile, StringComparer.OrdinalIgnoreCase))
            ?? installs.FirstOrDefault();

        var status = resolution.Outcome switch
        {
            ProfileResolution.Resolved when owner is not null => new PlcToolchainStatus
            {
                Available = true,
                Profile = resolution.Profile,
                Version = owner.FileVersion,
                ExecutablePath = owner.ExecutablePath,
                Explanation = $"{owner.InstallFolder} — {resolution.Profile}",
            },

            ProfileResolution.AmbiguousNeedsUserChoice => new PlcToolchainStatus
            {
                Available = false,
                Candidates = resolution.Candidates,
                Explanation =
                    $"{resolution.Candidates.Count} profiles installed — pick the one this " +
                    "repository targets before exporting",
            },

            _ => new PlcToolchainStatus
            {
                Available = false,
                Explanation = resolution.Explanation,
            },
        };

        if (status.Available)
        {
            _log.Info(CodesysLog.Category, $"toolchain ready: {status.Explanation}");
        }
        else
        {
            _log.Warn(CodesysLog.Category, $"toolchain unavailable: {status.Explanation}");
        }

        return Task.FromResult(status);
    }

    /// <summary>A runner for the install the probe found, or one that explains its absence.</summary>
    public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
        status is { Available: true, ExecutablePath: { } exe }
            ? new LocalCodesysRunner(exe, new ScriptExtractor(_log), _log)
            : new UnavailableToolchainRunner(
                status.Explanation ?? "the toolchain is not available on this machine");
}
