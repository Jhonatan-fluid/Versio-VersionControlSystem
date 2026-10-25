using System.Text.RegularExpressions;

namespace Versio.Core.Git;

/// <summary>
/// The files a commit is about to record that are large and covered by no LFS rule.
/// </summary>
/// <remarks>
/// <para>
/// The failure this prevents is expensive and hard to undo: a host rejects the <i>push</i>
/// on a size limit, by which time the object is already in history and removing it is a
/// rewrite. Asking at commit time is the last moment the answer is cheap. <c>.project</c>
/// files are exactly the shape at risk — binary, megabytes, and created by adopting a
/// repository that may never have had the LFS rules written.
/// </para>
/// <para>
/// <b>The pattern matcher is deliberately a subset of gitattributes semantics</b> — the
/// same trade the shallow parser in <see cref="GitAttributes"/> makes, stated so it is not
/// reused for something it cannot do. A pattern without a slash matches the file name in
/// any directory; one with a slash matches the whole repo-relative path; <c>*</c> stops at
/// a slash and <c>**</c> does not. That covers every rule Versio itself writes
/// (<c>*.project</c>, <c>mirror/**</c>) and the ordinary hand-written ones; character
/// classes and escapes inside a pattern are not understood, and a file protected by one of
/// those will be warned about anyway — a spurious question, not a lost file.
/// </para>
/// <para>
/// <b>What is not a subset is the order.</b> gitattributes is last-match-wins per
/// attribute, and the one way this preflight can fail <i>quietly</i> is to get that wrong:
/// the first version kept only the rules that said <c>filter=lfs</c> and asked whether any
/// of them matched, so <c>*.bin filter=lfs</c> followed by <c>huge.bin -filter</c> still
/// read as protected and the warning was suppressed for the one file git would commit in
/// full — a false negative, while the paragraph above promised only spurious questions.
/// The 2026-10-24 review's item 13. So every rule is walked in file order, and the last one
/// matching a path decides: <c>filter=lfs</c> protects, and <c>-filter</c>, <c>!filter</c>
/// or a different filter take it away again.
/// </para>
/// </remarks>
public static class LargeFilePreflight
{
    /// <summary>
    /// Ten megabytes.
    /// </summary>
    /// <remarks>
    /// Half an order of magnitude under the commonest host warning threshold (GitHub warns
    /// at 50 MB and blocks at 100 MB), because the right moment to hear about a growing
    /// binary is before the first host complains, not at the limit. No real exported mirror
    /// file approaches it; a <c>.project</c> can.
    /// </remarks>
    public const long DefaultThreshold = 10 * 1024 * 1024;

    /// <summary>The staged files over the threshold that no LFS rule matches.</summary>
    /// <param name="staged">Path and size of everything the commit would record.</param>
    /// <param name="gitattributes">The repository's <c>.gitattributes</c>, or empty.</param>
    public static IReadOnlyList<string> Unprotected(
        IEnumerable<(string Path, long Bytes)> staged,
        string gitattributes,
        long threshold = DefaultThreshold)
    {
        var rules = FilterRules(gitattributes);

        return staged
            .Where(f => f.Bytes >= threshold)
            .Where(f => !IsProtected(Normalize(f.Path), rules))
            .Select(f => f.Path)
            .ToList();
    }

    /// <summary>Whether the last rule that matches the path, and says anything about the filter, says lfs.</summary>
    private static bool IsProtected(string path, List<(Regex Pattern, bool Lfs)> rules)
    {
        var lfs = false;
        foreach (var (pattern, isLfs) in rules)
        {
            if (pattern.IsMatch(path))
            {
                lfs = isLfs;
            }
        }

        return lfs;
    }

    /// <summary>
    /// Every rule that says anything about the <c>filter</c> attribute, in file order, with
    /// whether it sets it to <c>lfs</c>.
    /// </summary>
    /// <remarks>
    /// A rule that says nothing about <c>filter</c> is left out rather than recorded as
    /// "not lfs": under git it leaves the attribute as an earlier rule set it, and recording
    /// it would make <c>*.project filter=lfs</c> followed by <c>*.project -diff</c> read as
    /// unprotected. A rule that says <c>binary</c> without lfs stops the merge corruption
    /// and does nothing for the host's size limit, so it does not count either.
    /// </remarks>
    private static List<(Regex Pattern, bool Lfs)> FilterRules(string gitattributes)
    {
        var rules = new List<(Regex, bool)>();

        foreach (var raw in (gitattributes ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // Read the way git reads it: a pattern with a space arrives quoted (the planner
            // writes it so since the 2026-10-24 review's item 26), and the first-whitespace
            // token of `"src/My App/*.project" filter=lfs` is `"src/My`, which protects nothing.
            if (GitAttributesPattern.Read(line) is not var (pattern, end))
            {
                continue;
            }

            if (FilterState(line[end..]) is { } lfs)
            {
                rules.Add((ToRegex(pattern), lfs));
            }
        }

        return rules;
    }

    /// <summary>
    /// What the attribute tokens after a pattern say about <c>filter</c>: true for
    /// <c>filter=lfs</c>, false for <c>-filter</c>, <c>!filter</c> or any other value, null
    /// when they do not mention it.
    /// </summary>
    /// <remarks>
    /// Whole tokens, compared exactly: the substring search this replaced would have taken
    /// <c>filter=lfsx</c> for protection. When one line names the attribute twice git takes
    /// the last, so this does too.
    /// </remarks>
    private static bool? FilterState(string attributes)
    {
        bool? state = null;

        foreach (var token in attributes.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "-filter" or "!filter")
            {
                state = false;
            }
            else if (token.StartsWith("filter=", StringComparison.Ordinal))
            {
                state = token == "filter=lfs";
            }
        }

        return state;
    }

    private static Regex ToRegex(string pattern)
    {
        var anchored = pattern.Contains('/');
        var trimmed = pattern.TrimStart('/');

        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '*' && i + 1 < trimmed.Length && trimmed[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');

        // A slash-free pattern matches the file's NAME wherever it sits — that is
        // gitattributes' own rule, and it is why "*.project" written at the root protects
        // a project three folders down.
        return new Regex(anchored ? sb.ToString() : $"^(.*/)?{sb.ToString()[1..]}",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
