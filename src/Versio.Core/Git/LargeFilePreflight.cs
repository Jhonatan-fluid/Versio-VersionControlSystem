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
/// (<c>*.project</c>, <c>mirror/**</c>) and the ordinary hand-written ones; negations,
/// character classes and escapes are not understood, and a file protected by one of those
/// will be warned about anyway — a spurious question, not a lost file.
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
        var rules = LfsPatterns(gitattributes);

        return staged
            .Where(f => f.Bytes >= threshold)
            .Where(f => !rules.Any(r => r.IsMatch(Normalize(f.Path))))
            .Select(f => f.Path)
            .ToList();
    }

    /// <summary>The patterns with <c>filter=lfs</c> on them, compiled for matching.</summary>
    private static List<Regex> LfsPatterns(string gitattributes)
    {
        var rules = new List<Regex>();

        foreach (var raw in (gitattributes ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // The pattern is the first token; "filter=lfs" anywhere after it is what makes
            // the rule protection. A rule that says binary without lfs stops the merge
            // corruption and does nothing for the host's size limit, so it does not count.
            var end = line.IndexOfAny([' ', '\t']);
            if (end < 0 || !line[end..].Contains("filter=lfs", StringComparison.Ordinal))
            {
                continue;
            }

            rules.Add(ToRegex(line[..end]));
        }

        return rules;
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
