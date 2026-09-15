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

    /// <summary>Patterns added to <c>.gitignore</c>, empty when it was already correct.</summary>
    public IReadOnlyList<string> GitIgnorePatterns { get; init; } = [];

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

    public string GitIgnorePath => Path.Combine(repositoryRoot, ".gitignore");

    /// <summary>Adopt a discovered project: guard it, write the attributes, write the binding.</summary>
    /// <param name="projectPath">Repo-relative, as the scanner reports it.</param>
    /// <param name="profile">Toolchain profile to pin, when one has been resolved.</param>
    public async Task<AdoptionResult> AdoptAsync(
        string projectPath, IGitClient? git, string? profile, CancellationToken ct)
    {
        var store = new RepoConfigStore(repositoryRoot, _log);
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

        var attributes = await EnsureGitAttributesAsync(
            git, ct, [decision.Binding!.ExportPath], [projectPath]).ConfigureAwait(false);
        var ignores = await EnsureGitIgnoreAsync(
            projectPath, ct, decision.Binding!.ExportPath).ConfigureAwait(false);
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
            GitIgnorePatterns = ignores.AddedPatterns,
            LfsConfigured = attributes.UsesLfs,
        };
    }

    /// <summary>
    /// Keep the toolchain's own scratch files out of git, creating <c>.gitignore</c> if needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on CODESYS 3.5.22.20: opening a project leaves <c>&lt;name&gt;-AllUsers.opt</c>
    /// and <c>&lt;name&gt;-&lt;user&gt;-&lt;machine&gt;.opt</c> beside it, and a <c>.~u</c> lock
    /// while it is held. Untracked, they appear in <c>git status</c>, in the project panel, and
    /// in anything that stages everything — and the per-user one puts a username and a machine
    /// name into history by its filename alone.
    /// </para>
    /// <para>
    /// <b>Created when absent</b>, because a repository with no <c>.gitignore</c> is the normal
    /// starting point for somebody who has kept PLC projects in a folder until now.
    /// </para>
    /// <para>
    /// The patterns come from the vendor and are scoped to this project, so adopting one
    /// CODESYS project does not quietly ignore every <c>.opt</c> file in somebody else's
    /// repository.
    /// </para>
    /// </remarks>
    /// <param name="exportPath">
    /// Where this project's mirror lives, so the scratch directories written <i>beside</i> it
    /// can be covered too. Null falls back to the conventional location, which is where a
    /// project adopted with default settings puts it.
    /// </param>
    public async Task<GitIgnorePlan> EnsureGitIgnoreAsync(
        string projectPath, CancellationToken ct, string? exportPath = null)
    {
        // Versio's own machine-specific directory first, and unconditionally: locks, session
        // state, backups and the scratch copies taken while the IDE holds a project. Three
        // comments in the engine described this as gitignored while nothing wrote the rule —
        // true here only because a person had typed it into this repository by hand.
        //
        // Then whatever the vendor leaves beside a project. The format is resolved from
        // the path when the caller did not pin one — pinning was how a mixed repository
        // wrote the first vendor's scratch rules beside every other brand's project.
        // And the two directories an export writes beside the mirror. Neither is the
        // vendor's: staging and trash are Versio's own swap mechanism, which is why they are
        // here rather than in TransientPatternsFor.
        //
        // Both are transient in the happy case and both survive a killed run. THIS
        // repository has the staging rule only because somebody typed it into .gitignore by
        // hand — an adopted user repository had nothing, so a killed export left an untracked
        // staging tree that "stage all" would cheerfully commit. Same shape as the three
        // comments that described .versio/local/ as gitignored while nothing wrote the rule.
        var resolved = format ?? PlcFormats.ForPath(projectPath);
        var mirror = (exportPath ?? (resolved is null
                ? BindingConventions.ConventionalExportPath(projectPath)
                : resolved.ConventionalExportPath(projectPath)))
            .Replace('\\', '/')
            .TrimEnd('/');

        List<string> patterns =
        [
            BindingConventions.LocalIgnorePattern,
            .. resolved?.TransientPatternsFor(projectPath) ?? [],
            mirror + BindingConventions.StagingSuffix + "/",
            mirror + BindingConventions.TrashSuffix + "/",
        ];

        var existing = File.Exists(GitIgnorePath)
            ? await File.ReadAllTextAsync(GitIgnorePath, ct).ConfigureAwait(false)
            : string.Empty;

        var creating = existing.Length == 0;
        var plan = GitIgnore.Plan(existing, patterns);

        if (!plan.ChangeNeeded)
        {
            _log.Debug(LogCategory.Git, ".gitignore already covers this project's scratch files");
            return plan;
        }

        await File.WriteAllTextAsync(GitIgnorePath, plan.Content, Utf8NoBom, ct)
            .ConfigureAwait(false);

        _log.Info(LogCategory.Git,
            $"{(creating ? "created .gitignore with" : "added")} " +
            $"{plan.AddedPatterns.Count} rule(s) for the toolchain's scratch files: " +
            string.Join(", ", plan.AddedPatterns));

        return plan;
    }

    /// <summary>Add the binary/LFS rules for this vendor's project files, if they are absent.</summary>
    /// <param name="mirrors">
    /// Export directories that need <c>text eol=lf</c>, so a clone with
    /// <c>core.autocrlf=true</c> lands the mirror byte-identical to what the exporter wrote.
    /// </param>
    /// <param name="projects">
    /// Repo-relative project paths whose binary store beside the pointer needs its own rules
    /// — see <see cref="IPlcProjectFormat.BinaryPayloadPatternsFor"/>. Empty for a vendor whose
    /// project is a single file.
    /// </param>
    public async Task<GitAttributesPlan> EnsureGitAttributesAsync(
        IGitClient? git, CancellationToken ct, IReadOnlyList<string>? mirrors = null,
        IReadOnlyList<string>? projects = null)
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
        //
        // Then each project's binary store, scoped to that project. The extension rule covers
        // a file project completely and a directory project hardly at all: a TIA Portal
        // repository whose only rule was *.ap20 protected the 150 KB pointer and committed the
        // half-gigabyte System/PEData.plf beside it as an ordinary git blob. Resolved per
        // path, the same way adoption resolves the vendor, so a mixed repository asks each
        // project's own format.
        var payload = (projects ?? [])
            .SelectMany(p => (format ?? PlcFormats.ForPath(p))?.BinaryPayloadPatternsFor(p) ?? [])
            .Select(p => p.Replace('\\', '/'));
        var patterns = PlcFormats.AllExtensions.Select(e => $"*{e}").Concat(payload).ToList();
        var plan = GitAttributes.Plan(existing, patterns, lfs, mirrors);

        if (plan.Withheld.Count > 0)
        {
            // Warn, because the consequence is a folder git stores as text. The alternative —
            // a binary rule over the mirror — makes the reviewable export undiffable, which is
            // worse, and the commit's large-file check still asks before a big file lands
            // outside LFS.
            _log.Warn(LogCategory.Git,
                $"left {string.Join(", ", plan.Withheld)} without a binary rule: " +
                "each would also cover a mirror, and a binary rule over the mirror makes " +
                "the export undiffable");
        }

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
