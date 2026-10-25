using System.Text;
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
/// <b>The pattern matcher follows gitattributes(5), not a subset of it.</b> It used to be
/// a deliberate subset — slash anchoring, <c>*</c>, <c>?</c> and <c>**</c> — on the
/// argument that a rule it could not read would be warned about anyway: a spurious
/// question, not a lost file. That argument only holds for a rule that <i>grants</i>
/// protection. A rule that takes it away (<c>huge[.]bin -filter</c> after
/// <c>*.bin filter=lfs</c>) and is not understood leaves the earlier grant standing, and
/// the warning is suppressed for the one file git will commit in full. And a rule read
/// <i>wider</i> than git reads it (<c>a**b.bin</c> taken as any depth, when git takes a
/// <c>**</c> that is not between slashes as a plain <c>*</c>) grants protection git never
/// gives. The 2026-10-25 review's item 5, after item 13 of the day before had made the
/// matcher's order right and left its vocabulary short. So the vocabulary is now git's:
/// character classes with ranges, <c>!</c>/<c>^</c> negation and the POSIX names, backslash
/// escapes, <c>**</c> only between slashes or ends, and git's two "never matches" cases — a
/// class that never closes and a trailing backslash — never match here either. Every rule
/// below was asked of <c>git check-attr</c> before it was written down, and the test's
/// table is that transcript.
/// </para>
/// <para>
/// <b>Last match wins, per attribute.</b> gitattributes is last-match-wins, and the one
/// way this preflight can fail <i>quietly</i> is to get that wrong: the first version kept
/// only the rules that said <c>filter=lfs</c> and asked whether any of them matched, so
/// <c>*.bin filter=lfs</c> followed by <c>huge.bin -filter</c> still read as protected and
/// the warning was suppressed for the one file git would commit in full — a false
/// negative, while the class's remarks promised only spurious questions. The 2026-10-24
/// review's item 13. So every rule is walked in file order, and the last one matching a
/// path decides: <c>filter=lfs</c> protects, and <c>-filter</c>, <c>!filter</c>, a bare
/// <c>filter</c> or a different value take it away again.
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
            .Where(f => !IsProtected(ByteForm(Normalize(f.Path)), rules))
            .Select(f => f.Path)
            .ToList();
    }

    /// <summary>
    /// The text as wildmatch walks it: one character per UTF-8 byte, the ASCII ones as
    /// themselves and the rest moved into the private-use area.
    /// </summary>
    /// <remarks>
    /// git matches a pattern against a path byte by byte, so <c>?</c> and a character class
    /// each consume one byte of the path's UTF-8 — ä is two of them, 😀 four — and a
    /// literal ä in the pattern is its two bytes. Matching .NET strings walked UTF-16
    /// characters, one per ä, so <c>?.bin filter=lfs</c>, which git does not apply to ä.bin,
    /// read as protecting it and the warning was suppressed for a file git commits in full;
    /// and <c>?.bin -filter</c> after <c>*.bin filter=lfs</c>, which git ignores for ä.bin,
    /// took its protection away here (the 2026-10-25 review's third round, item 3). Pattern
    /// and path both pass through this, so a literal still names its file. Bytes 0x80–0xFF
    /// become U+E080–U+E0FF rather than Latin-1 characters, because <c>IgnoreCase</c> folds
    /// Latin-1 letters and git's folding is ASCII-only; the private-use area has no case,
    /// and it keeps the bytes' order, so a range that crosses 0x80 still compares as git's
    /// bytes do.
    /// </remarks>
    private static string ByteForm(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            sb.Append(b < 0x80 ? (char)b : (char)(0xE000 + b));
        }

        return sb.ToString();
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
    /// and does nothing for the host's size limit, so it does not count either. A rule
    /// whose pattern git would never match (<see cref="ToRegex"/> returns null) is left out
    /// for the same reason: it changes nothing under git, so it must change nothing here.
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

            if (FilterState(line[end..]) is { } lfs && ToRegex(pattern) is { } regex)
            {
                rules.Add((regex, lfs));
            }
        }

        return rules;
    }

    /// <summary>
    /// What the attribute tokens after a pattern say about <c>filter</c>: true for
    /// <c>filter=lfs</c>, false for <c>-filter</c>, <c>!filter</c>, a bare <c>filter</c> or
    /// any other value, null when they do not mention it.
    /// </summary>
    /// <remarks>
    /// Whole tokens, compared exactly: the substring search this replaced would have taken
    /// <c>filter=lfsx</c> for protection. When one line names the attribute twice git takes
    /// the last, so this does too. The bare spelling is the one that was missing: git reads
    /// <c>huge.bin filter</c> as the attribute set to true — <c>check-attr</c> answers
    /// <c>set</c> — and no filter driver is called "true", so the content is committed in
    /// full; letting it fall through as "says nothing" left an earlier lfs rule standing.
    /// The 2026-10-25 review's item 5.
    /// </remarks>
    private static bool? FilterState(string attributes)
    {
        bool? state = null;

        foreach (var token in attributes.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "filter" or "-filter" or "!filter")
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

    /// <summary>
    /// The pattern as git's wildmatch would read it, or null for one git can never match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// gitattributes matches with <c>WM_PATHNAME</c>: nothing but a slash-delimited
    /// <c>**</c> crosses a slash — not <c>*</c>, not <c>?</c>, and not a character class
    /// even when the class lists the slash or spans it in a range (<c>a[/]b</c> and
    /// <c>a[+-9]b</c> both leave <c>a/b</c> unmatched). A run of two or more stars is "any
    /// depth" only when a slash or the pattern's end sits on both sides of it; anywhere
    /// else it is one star, so <c>a**b.bin</c> matches <c>axb.bin</c> and not <c>a/b.bin</c>,
    /// and <c>a/***.bin</c> stays inside <c>a/</c>.
    /// </para>
    /// <para>
    /// Null is git's "abort": a <c>[</c> that never closes, a backslash with nothing after
    /// it, or a POSIX class name git does not know make wildmatch give up on the pattern,
    /// and the line then matches nothing at all — not even a file literally called
    /// <c>huge[.bin</c>. Treating the bracket as a literal instead would have been the
    /// friendlier reading and the wrong one.
    /// </para>
    /// <para>
    /// Case-insensitive, because Versio runs on Windows where git's <c>core.ignorecase</c>
    /// defaults on and gitattributes folds with it, inside classes too (<c>v[a-c].bin</c>
    /// matches <c>vB.bin</c>).
    /// </para>
    /// <para>
    /// Over bytes, not characters: the pattern is matched in its <see cref="ByteForm"/>
    /// against the path's, so <c>?</c> is one byte of UTF-8 as it is for git, and a
    /// non-ASCII file name is matched the way git matches it rather than the way .NET would.
    /// </para>
    /// </remarks>
    private static Regex? ToRegex(string pattern)
    {
        // Any slash, leading or not, anchors the pattern at the .gitattributes folder — the
        // repository root here. A leading one is then just that anchor spelled out.
        var anchored = pattern.Contains('/');
        var glob = ByteForm(pattern.TrimStart('/'));

        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 >= glob.Length)
                    {
                        return null;
                    }

                    sb.Append(Regex.Escape(glob[++i].ToString()));
                    break;

                case '*':
                    var run = i;
                    while (run < glob.Length && glob[run] == '*')
                    {
                        run++;
                    }

                    var slashBefore = i == 0 || glob[i - 1] == '/';
                    var slashAfter = run == glob.Length || glob[run] == '/';
                    if (run - i >= 2 && slashBefore && slashAfter)
                    {
                        if (run == glob.Length)
                        {
                            // Trailing "/**": everything beneath, however deep.
                            sb.Append(".*");
                            i = run - 1;
                        }
                        else
                        {
                            // "**/": zero or more whole folders, the slash included.
                            sb.Append("(?:.*/)?");
                            i = run;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i = run - 1;
                    }

                    break;

                case '?':
                    sb.Append("[^/]");
                    break;

                case '[':
                    if (CharacterClass(glob, ref i) is not { } cls)
                    {
                        return null;
                    }

                    sb.Append(cls);
                    break;

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        // A slash-free pattern matches the file's NAME wherever it sits — that is
        // gitattributes' own rule, and it is why "*.project" written at the root protects
        // a project three folders down.
        var prefix = anchored ? "^" : "^(?:.*/)?";
        return new Regex($"{prefix}{sb}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The bracket expression starting at <paramref name="i"/>, as a regex class, with
    /// <paramref name="i"/> left on its closing bracket. Null when it never closes.
    /// </summary>
    /// <remarks>
    /// The shapes are wildmatch's, each checked against git: <c>!</c> or <c>^</c> first
    /// negates; a <c>]</c> first (after the negation) is a member, not the close; a
    /// <c>-</c> first or last is a member, not a range; a backslash escapes the next
    /// character; <c>[:digit:]</c> and its siblings are the C <c>is*</c> sets. A range's low
    /// end is a member in its own right, so one written backwards (<c>[z-a]</c>) is its low
    /// end alone — not nothing, and not the .NET parse error that dropping it produced. The
    /// slash is kept out of every class by hand, because the regex engine does not know it
    /// is a separator.
    /// </remarks>
    private static string? CharacterClass(string glob, ref int i)
    {
        var sb = new StringBuilder();
        var j = i + 1;
        var negated = j < glob.Length && glob[j] is '!' or '^';
        if (negated)
        {
            j++;
        }

        var first = true;
        while (j < glob.Length)
        {
            var c = glob[j];
            if (c == ']' && !first)
            {
                i = j;
                return negated ? $"[^/{sb}]" : $"(?!/)[{sb}]";
            }

            first = false;
            if (c == '[' && j + 1 < glob.Length && glob[j + 1] == ':')
            {
                var close = glob.IndexOf(":]", j + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    // Not a POSIX class after all: wildmatch takes the bracket literally.
                    sb.Append("\\[");
                    j++;
                    continue;
                }

                if (PosixClass(glob[(j + 2)..close]) is not { } members)
                {
                    return null;
                }

                sb.Append(members);
                j = close + 2;
                continue;
            }

            if (c == '\\' && j + 1 < glob.Length)
            {
                c = glob[++j];
            }

            // A range needs a member on both sides; "a-]" is the two members a and -.
            if (j + 2 < glob.Length && glob[j + 1] == '-' && glob[j + 2] != ']')
            {
                var hi = glob[j + 2];
                if (hi == '\\' && j + 3 < glob.Length)
                {
                    hi = glob[j + 3];
                    j++;
                }

                // wildmatch tests the low end as a member before it reads the dash, so a
                // range is its low end plus everything up to its high end — and one written
                // backwards is just its low end: `[z-a]` matches z and nothing else, and
                // `[!z-a]` everything but z. The first version dropped a reversed range
                // whole, which left `[]` for .NET to refuse, and preflight threw on the rule
                // instead of weighing the commit (the 2026-10-25 review's third round, item 2).
                sb.Append(ClassLiteral(c));
                if (c < hi)
                {
                    sb.Append('-').Append(ClassLiteral(hi));
                }

                j += 3;
                continue;
            }

            sb.Append(ClassLiteral(c));
            j++;
        }

        return null;
    }

    private static string ClassLiteral(char c) =>
        c is '\\' or ']' or '[' or '^' or '-' ? $"\\{c}" : c.ToString();

    /// <summary>The members of a named POSIX class, as the C locale defines them.</summary>
    private static string? PosixClass(string name) => name switch
    {
        "alnum" => "a-zA-Z0-9",
        "alpha" => "a-zA-Z",
        "blank" => " \\t",
        "cntrl" => "\\x00-\\x1F\\x7F",
        "digit" => "0-9",
        "graph" => "\\x21-\\x7E",
        "lower" => "a-z",
        "print" => "\\x20-\\x7E",
        "punct" => "!-/:-@\\[-`{-~",
        "space" => " \\t\\n\\v\\f\\r",
        "upper" => "A-Z",
        "xdigit" => "0-9A-Fa-f",
        _ => null,
    };

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
