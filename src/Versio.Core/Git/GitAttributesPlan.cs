namespace Versio.Core.Git;

/// <summary>What adopting a project needs written into <c>.gitattributes</c>, if anything.</summary>
public readonly record struct GitAttributesPlan(
    bool ChangeNeeded,
    string Content,
    IReadOnlyList<string> AddedPatterns,
    bool UsesLfs)
{
    /// <summary>
    /// Binary patterns that were asked for and deliberately not written, because each would
    /// also have covered a mirror.
    /// </summary>
    /// <remarks>
    /// Reported rather than dropped quietly: a folder left without its binary rule is a folder
    /// git will store as text, and the caller says so in the log.
    /// </remarks>
    public IReadOnlyList<string> Withheld { get; init; } = [];

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

    private const string MirrorHeader =
        "# Added by Versio: the mirror is text, and must arrive byte-identical everywhere.";

    private const string MirrorHeader2 =
        "# Without eol=lf a core.autocrlf=true clone lands CRLF, which git calls clean and";

    private const string MirrorHeader3 =
        "# Versio's hash gate calls an edit to every file in it.";

    private const string Header2 =
        "# binary -diff -merge stops git from attempting a line merge and corrupting it.";

    /// <param name="existing">Current file contents, or empty when there is no file.</param>
    /// <param name="patterns">Glob patterns to cover, e.g. <c>*.project</c>.</param>
    /// <param name="lfsAvailable">Whether <c>git lfs</c> is installed and usable here.</param>
    /// <param name="mirrors">
    /// Export directories, which need the opposite treatment: <c>text eol=lf</c>.
    /// </param>
    /// <remarks>
    /// <b>Why the mirror needs a rule at all, measured rather than assumed.</b> With
    /// <c>core.autocrlf=true</c> — the Windows default in many installations — a clone lands
    /// the mirror with CRLF endings. Git itself stays happy: <c>git status</c> reports the
    /// tree clean, because git normalises on the way back in. <b>Versio does not.</b> The
    /// exporter writes LF and the hash gate hashes the bytes on disk, so on such a clone every
    /// file in the mirror hashes differently from what an export would produce:
    /// <c>6fb69762…</c> against <c>c58f937f…</c> for a two-line file. The engine would read a
    /// teammate's fresh clone as a mirror that had been edited by hand.
    /// <para>
    /// Checked against real git in a scratch repository, both ways: without the rule the
    /// working copy is <c>0d0a</c>-terminated and hashes differ; with it the clone is
    /// byte-identical to what was committed, same SHA, with <c>core.autocrlf=true</c> still on.
    /// </para>
    /// </remarks>
    public static GitAttributesPlan Plan(
        string existing,
        IReadOnlyList<string> patterns,
        bool lfsAvailable,
        IReadOnlyList<string>? mirrors = null)
    {
        var covered = ExistingPatterns(existing);

        var mirrorRoots = (mirrors ?? [])
            .Select(m => m.Replace('\\', '/').TrimEnd('/'))
            .Where(m => m.Length > 0)
            .ToList();

        var candidates = patterns
            .Where(p => !covered.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // NEVER A BINARY RULE OVER A MIRROR.
        //
        // A directory project keeps its mirror inside its own folder, so a folder-scoped
        // binary rule can reach the export - and later attributes win, so appending one after
        // the mirror's text rule silently undoes it. Measured on this repository's own TIA
        // fixture, where a hand-written folder rule did exactly that: every mirror file
        // resolved to diff unset and printed "Binary files differ". Withheld rather than
        // written-and-overridden, because the planner never rewrites a line somebody already
        // has, so it cannot guarantee its override would land after theirs.
        var withheld = candidates
            .Where(p => mirrorRoots.Any(m => CoversDirectory(p, m)))
            .ToList();

        var missing = candidates
            .Where(p => !withheld.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var missingMirrors = mirrorRoots
            .Select(m => m + "/**")
            .Where(p => !covered.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0 && missingMirrors.Count == 0)
        {
            return GitAttributesPlan.NoChange(existing) with { Withheld = withheld };
        }

        var width = missing.Concat(missingMirrors).Max(p => p.Length);

        // NO merge=lfs, AND THAT OMISSION IS THE WHOLE POINT OF THIS LINE.
        //
        // It used to read "filter=lfs diff=lfs merge=lfs", which is what every LFS tutorial
        // prints - and later attributes win, so merge=lfs SILENTLY OVERRODE the -merge two
        // tokens earlier. `git check-attr merge` on a .project answered `lfs`, not `unset`,
        // on every machine with LFS installed: precisely the machines the LFS tokens were
        // added for.
        //
        // What that cost, measured on two throwaway repositories differing only in this line:
        //
        //   -diff -merge                    warning: Cannot merge binary files
        //                                   working tree keeps the intact binary, conflict raised
        //
        //   -merge ... merge=lfs            no warning, git text-merges the POINTER
        //                                   working tree gets an LFS pointer with <<<<<<< in it
        //
        // merge=lfs names a driver that git-lfs does not install - there is no
        // merge.lfs.driver in anybody's config - so git falls back to the built-in three-way
        // text merge. Naming a driver that does not exist is worse than naming none, because
        // it is what disables the protection that does.
        //
        // filter=lfs and diff=lfs stay: those name real, installed behaviours and are what
        // make the binary stored and shown sensibly. Only the merge token goes.
        var rules = missing.Select(p =>
            $"{p.PadRight(width)} binary -diff -merge" +
            (lfsAvailable ? " filter=lfs diff=lfs" : string.Empty));

        // The opposite rule, for the opposite reason: the mirror exists to be diffed and
        // merged, and it has to arrive byte-identical on every machine that clones it.
        var mirrorRules = missingMirrors.Select(p => $"{p.PadRight(width)} text eol=lf");

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

        if (missing.Count > 0)
        {
            builder.Append(Header).Append('\n').Append(Header2).Append('\n');
            foreach (var rule in rules)
            {
                builder.Append(rule).Append('\n');
            }
        }

        if (missingMirrors.Count > 0)
        {
            if (missing.Count > 0)
            {
                builder.Append('\n');
            }

            builder.Append(MirrorHeader).Append('\n')
                   .Append(MirrorHeader2).Append('\n')
                   .Append(MirrorHeader3).Append('\n');
            foreach (var rule in mirrorRules)
            {
                builder.Append(rule).Append('\n');
            }
        }

        return new GitAttributesPlan(
            true, builder.ToString(), [.. missing, .. missingMirrors], lfsAvailable)
        {
            Withheld = withheld,
        };
    }

    /// <summary>
    /// True when a <c>dir/**</c> pattern reaches <paramref name="directory"/> or anything in it.
    /// </summary>
    /// <remarks>
    /// Only the <c>/**</c> shape is judged, because it is the only shape a folder-scoped rule
    /// takes and the only one that can swallow a whole mirror; an extension pattern such as
    /// <c>*.project</c> never names an export directory. Case-insensitive, which withholds more
    /// on a case-sensitive filesystem rather than less — the side that keeps the mirror text.
    /// </remarks>
    private static bool CoversDirectory(string pattern, string directory)
    {
        if (!pattern.EndsWith("/**", StringComparison.Ordinal))
        {
            return false;
        }

        var root = pattern[..^3].TrimStart('/');
        return root.Length == 0
               || string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
               || directory.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
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
