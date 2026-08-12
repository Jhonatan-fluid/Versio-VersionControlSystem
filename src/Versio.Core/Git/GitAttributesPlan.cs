namespace Versio.Core.Git;

/// <summary>What adopting a project needs written into <c>.gitattributes</c>, if anything.</summary>
public readonly record struct GitAttributesPlan(
    bool ChangeNeeded,
    string Content,
    IReadOnlyList<string> AddedPatterns,
    bool UsesLfs)
{
    public static GitAttributesPlan NoChange(string content) =>
        new(false, content, [], false);
}

/// <summary>
/// Plans the <c>.gitattributes</c> rules a tracked PLC project needs.
/// </summary>
/// <remarks>
/// <para>
/// Without these rules git treats a <c>.project</c> as text: it will attempt a line merge on
/// a binary compound document and produce a file that opens in neither CODESYS nor a text
/// editor. <c>binary -diff -merge</c> is what stops that, and it is the part that matters —
/// LFS is a storage optimisation layered on top.
/// </para>
/// <para>
/// <b>LFS is only written when git can actually run it.</b> Writing <c>filter=lfs</c> into a
/// repository on a machine without LFS installed does not degrade gracefully: every
/// subsequent checkout and commit fails on a missing filter, and the person who hits it did
/// not choose LFS and has no idea where the rule came from. The caller probes first and
/// passes the answer in.
/// </para>
/// <para>
/// <b>An existing rule for a pattern is never rewritten.</b> A repository that already says
/// something about <c>*.project</c> has been configured deliberately — possibly to opt out
/// of LFS — and silently overriding that would be a worse bug than the one being fixed.
/// </para>
/// </remarks>
public static class GitAttributes
{
    private const string Header =
        "# Added by Versio: a PLC project is a binary compound document. Marking it";

    private const string Header2 =
        "# binary -diff -merge stops git from attempting a line merge and corrupting it.";

    /// <param name="existing">Current file contents, or empty when there is no file.</param>
    /// <param name="patterns">Glob patterns to cover, e.g. <c>*.project</c>.</param>
    /// <param name="lfsAvailable">Whether <c>git lfs</c> is installed and usable here.</param>
    public static GitAttributesPlan Plan(
        string existing, IReadOnlyList<string> patterns, bool lfsAvailable)
    {
        var covered = ExistingPatterns(existing);
        var missing = patterns
            .Where(p => !covered.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            return GitAttributesPlan.NoChange(existing);
        }

        var width = missing.Max(p => p.Length);
        var rules = missing.Select(p =>
            $"{p.PadRight(width)} binary -diff -merge" +
            (lfsAvailable ? " filter=lfs diff=lfs merge=lfs" : string.Empty));

        // LF regardless of platform: .gitattributes is read by git, and CRLF in the file it
        // uses to decide line endings is the kind of irony that costs an afternoon.
        var builder = new System.Text.StringBuilder();
        if (existing.Length > 0)
        {
            builder.Append(existing.Replace("\r\n", "\n"));
            if (!existing.EndsWith('\n'))
            {
                builder.Append('\n');
            }

            builder.Append('\n');
        }

        builder.Append(Header).Append('\n').Append(Header2).Append('\n');
        foreach (var rule in rules)
        {
            builder.Append(rule).Append('\n');
        }

        return new GitAttributesPlan(true, builder.ToString(), missing, lfsAvailable);
    }

    /// <summary>
    /// Patterns the file already says something about.
    /// </summary>
    /// <remarks>
    /// A deliberately shallow parse: the first whitespace-delimited token of a non-comment
    /// line is the pattern. Enough to answer "has anyone already spoken about
    /// <c>*.project</c>", which is the only question asked. It does not try to understand
    /// the attributes, because acting on them would mean deciding a user's rule is wrong.
    /// </remarks>
    private static HashSet<string> ExistingPatterns(string content)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("[attr]", StringComparison.Ordinal))
            {
                continue;
            }

            var end = line.IndexOfAny([' ', '\t']);
            found.Add(end < 0 ? line : line[..end]);
        }

        return found;
    }
}
