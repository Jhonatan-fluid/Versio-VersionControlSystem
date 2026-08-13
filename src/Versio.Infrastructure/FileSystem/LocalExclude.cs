using System.Text;
using Versio.Core.Bindings;
using Versio.Core.Git;
using Versio.Core.Logging;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// Keeps Versio's own scratch directory invisible to git, per clone.
/// </summary>
/// <remarks>
/// <para>
/// <c>.git/info/exclude</c>, <b>not</b> <c>.gitignore</c>, and the difference is the whole
/// point. <c>.gitignore</c> is a tracked file: it is committed, it travels, it appears in
/// diffs — and it can be carried away by a stash, which is exactly when this rule has to hold.
/// The auto-stash record lives in <c>.versio/local/</c>, so a stash taken while that directory
/// was visible to git would swallow the record naming the previous stash, leaving real work in
/// git with nothing pointing at it. <c>info/exclude</c> is outside the working tree and cannot
/// be stashed, committed or conflicted.
/// </para>
/// <para>
/// Adoption still writes <c>.versio/local/</c> into <c>.gitignore</c> as well, and that is not
/// redundant: it is the rule a <i>teammate</i> gets when they clone. This is the one that
/// protects <i>this</i> clone from the moment Versio first writes anything, which is before
/// any project has been adopted.
/// </para>
/// <para>
/// The same planner as <c>.gitignore</c> decides what to append, so an existing rule — or a
/// deliberate negation somebody typed — is honoured rather than duplicated.
/// </para>
/// </remarks>
public static class LocalExclude
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Make sure this clone excludes <c>.versio/local/</c>. Never throws.</summary>
    /// <param name="gitDirectory">Absolute path of the <c>.git</c> directory.</param>
    public static async Task EnsureAsync(
        string gitDirectory, IVersioLogger? logger, CancellationToken ct)
    {
        var log = logger ?? NullLogger.Instance;

        if (string.IsNullOrEmpty(gitDirectory) || !Directory.Exists(gitDirectory))
        {
            return;
        }

        var path = Path.Combine(gitDirectory, "info", "exclude");

        try
        {
            var existing = File.Exists(path)
                ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)
                : string.Empty;

            var plan = GitIgnore.Plan(existing, [BindingConventions.LocalIgnorePattern]);
            if (!plan.ChangeNeeded)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, plan.Content, Utf8NoBom, ct).ConfigureAwait(false);

            log.Info(LogCategory.Git,
                $"excluded {BindingConventions.LocalIgnorePattern} in this clone via {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or unusual .git is not worth failing an open over. The consequence
            // is cosmetic until a stash is taken, and the stash path says so for itself.
            log.Warn(LogCategory.Git, $"could not write {path}: {ex.Message}");
        }
    }
}
