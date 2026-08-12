using System.Text;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Plc;

namespace Versio.Infrastructure.Bindings;

/// <summary>What adopting a project actually did.</summary>
public sealed record AdoptionResult
{
    public required AdoptionDecision Decision { get; init; }

    /// <summary>The config as it stands after the write. Unchanged when the adoption was
    /// refused.</summary>
    public required RepoConfig Config { get; init; }

    /// <summary>Patterns added to <c>.gitattributes</c>, empty when it was already correct.</summary>
    public IReadOnlyList<string> GitAttributePatterns { get; init; } = [];

    public bool LfsConfigured { get; init; }

    public bool Adopted => Decision.CanAdopt;
}

/// <summary>
/// Performs the filesystem half of adopting a project.
/// </summary>
/// <remarks>
/// <para>
/// The decision of <i>whether</i> to adopt lives in <see cref="Adoption"/>, pure and unit
/// tested. This class only carries it out, in a fixed order chosen so a failure never leaves
/// a half-adopted repository: <c>.gitattributes</c> is written <b>before</b> the binding,
/// because a binding whose project git will happily line-merge is the dangerous state, while
/// an attributes rule with no binding yet is harmless.
/// </para>
/// <para>
/// No export is run here. Adoption is a cheap, reversible edit to two text files; the first
/// export costs 17–31 s of CODESYS and is the user's next, separate decision.
/// </para>
/// </remarks>
/// <remarks>
/// The format is resolved <b>from the path</b> rather than assumed, so adopting a TwinCAT
/// project in a repository that also holds a CODESYS one records the right vendor on each
/// binding. A caller may still pin one.
/// </remarks>
public sealed class RepoSetup(
    string repositoryRoot,
    IPlcProjectFormat? format = null,
    IVersioLogger? logger = null)
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string GitAttributesPath => Path.Combine(repositoryRoot, ".gitattributes");

    /// <summary>Adopt a discovered project: guard it, write the attributes, write the binding.</summary>
    /// <param name="projectPath">Repo-relative, as the scanner reports it.</param>
    /// <param name="profile">Toolchain profile to pin, when one has been resolved.</param>
    public async Task<AdoptionResult> AdoptAsync(
        string projectPath, IGitClient? git, string? profile, CancellationToken ct)
    {
        var store = new RepoConfigStore(repositoryRoot);
        var config = await store.LoadAsync(ct).ConfigureAwait(false);

        // Whichever vendor claims the extension. A pinned format still wins, and nothing
        // claiming it at all is an explicit refusal rather than a CODESYS assumption.
        var resolved = format ?? PlcFormats.ForPath(projectPath);
        if (resolved is null)
        {
            return new AdoptionResult
            {
                Decision = new AdoptionDecision(
                    AdoptionVerdict.NotAProject,
                    $"no registered toolchain recognises '{projectPath}'", null),
                Config = config,
            };
        }

        var decision = Adoption.Plan(
            projectPath, config, resolved, Guid.NewGuid().ToString("N")[..12], profile);

        if (!decision.CanAdopt)
        {
            _log.Warn(LogCategory.Sync,
                $"refused to adopt {projectPath}: {decision.Explanation}");
            return new AdoptionResult { Decision = decision, Config = config };
        }

        var attributes = await EnsureGitAttributesAsync(git, ct).ConfigureAwait(false);
        var updated = await store.AddBindingAsync(decision.Binding!, ct).ConfigureAwait(false);

        // Adoption edits two tracked files and creates a binding the user will live with,
        // so the whole result is stated in one line rather than reconstructed from three.
        _log.Info(LogCategory.Sync,
            $"adopted {projectPath} as binding {decision.Binding!.Id} " +
            $"exporting to {decision.Binding!.ExportPath}" +
            (profile is null ? " (no profile pinned)" : $" [{profile}]"));

        return new AdoptionResult
        {
            Decision = decision,
            Config = updated,
            GitAttributePatterns = attributes.AddedPatterns,
            LfsConfigured = attributes.UsesLfs,
        };
    }

    /// <summary>Add the binary/LFS rules for this vendor's project files, if they are absent.</summary>
    public async Task<GitAttributesPlan> EnsureGitAttributesAsync(
        IGitClient? git, CancellationToken ct)
    {
        var existing = File.Exists(GitAttributesPath)
            ? await File.ReadAllTextAsync(GitAttributesPath, ct).ConfigureAwait(false)
            : string.Empty;

        // Probed, never assumed. Without git we cannot know, and guessing "yes" is the
        // answer that breaks the repository.
        var lfs = git is not null && await git.IsLfsAvailableAsync(ct).ConfigureAwait(false);

        // Every registered vendor's extensions, not just the one being adopted: the rules are
        // repository-wide, and writing them one vendor at a time would leave a TwinCAT project
        // line-mergeable until somebody happened to adopt one.
        var patterns = PlcFormats.AllExtensions.Select(e => $"*{e}").ToList();
        var plan = GitAttributes.Plan(existing, patterns, lfs);

        if (plan.ChangeNeeded)
        {
            await File.WriteAllTextAsync(GitAttributesPath, plan.Content, Utf8NoBom, ct)
                .ConfigureAwait(false);

            // Which rules were added, and whether LFS is behind them. Getting this wrong is
            // what lets git line-merge a binary project, so the record matters later.
            _log.Info(LogCategory.Git,
                $"wrote {plan.AddedPatterns.Count} rule(s) to .gitattributes " +
                $"({(lfs ? "with LFS" : "binary only, LFS unavailable")}): " +
                string.Join(", ", plan.AddedPatterns));
        }
        else
        {
            _log.Debug(LogCategory.Git, ".gitattributes already covers this project type");
        }

        return plan;
    }
}
