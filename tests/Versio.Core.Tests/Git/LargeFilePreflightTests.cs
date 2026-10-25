using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Which staged files earn the large-file question, and which LFS rules count as protection.
/// </summary>
public sealed class LargeFilePreflightTests
{
    private const long Mb = 1024 * 1024;

    private const string VersioRules =
        "# Added by Versio\n" +
        "*.project binary -diff -merge filter=lfs diff=lfs merge=lfs\n" +
        "plc/Machine/** text eol=lf\n";

    [Fact]
    public void A_large_file_with_no_rules_at_all_is_reported()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("plc/Machine.project", 20 * Mb)], gitattributes: "");

        Assert.Equal("plc/Machine.project", Assert.Single(offenders));
    }

    [Fact]
    public void The_rule_Versio_itself_writes_protects_a_nested_project()
    {
        // "*.project" has no slash, so it matches the file name in any directory —
        // gitattributes' own rule, and the reason adoption's rule written at the root
        // covers a project three folders down.
        var offenders = LargeFilePreflight.Unprotected(
            [("plc/deep/nested/Machine.project", 20 * Mb)], VersioRules);

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_rule_without_lfs_is_not_protection()
    {
        // "binary -diff -merge" stops the merge corruption and does nothing for a host's
        // size limit. Counting it would silence the warning for exactly the repositories
        // adopted on a machine without LFS — the ones most at risk.
        var offenders = LargeFilePreflight.Unprotected(
            [("plc/Machine.project", 20 * Mb)],
            "*.project binary -diff -merge\n");

        Assert.Equal("plc/Machine.project", Assert.Single(offenders));
    }

    [Fact]
    public void A_small_file_is_nobody_s_business()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("plc/Machine.project", LargeFilePreflight.DefaultThreshold - 1)],
            gitattributes: "");

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_threshold_is_inclusive()
    {
        // Exactly at the line is over the line: the threshold names the size at which the
        // question starts being worth asking, and off-by-one here shows up as a file that
        // grew past every warning without ever being asked about.
        var offenders = LargeFilePreflight.Unprotected(
            [("a.bin", LargeFilePreflight.DefaultThreshold)], gitattributes: "");

        Assert.Single(offenders);
    }

    [Fact]
    public void A_path_rule_with_double_star_reaches_the_whole_subtree()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("assets/video/intro.mp4", 60 * Mb)],
            "assets/** filter=lfs diff=lfs merge=lfs\n");

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_single_star_does_not_cross_directories()
    {
        // "assets/*" protects files directly inside assets and nothing deeper — the same
        // distinction git itself draws, and the difference between a rule that works and a
        // rule that looks like it works.
        var offenders = LargeFilePreflight.Unprotected(
            [("assets/video/intro.mp4", 60 * Mb)],
            "assets/* filter=lfs\n");

        Assert.Single(offenders);
    }

    [Fact]
    public void Backslashes_and_case_do_not_defeat_a_rule()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [(@"plc\MACHINE.PROJECT", 20 * Mb)], VersioRules);

        Assert.Empty(offenders);
    }

    [Fact]
    public void Comments_and_blank_lines_are_not_rules()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("a.bin", 20 * Mb)],
            "\n# *.bin filter=lfs\n\n");

        Assert.Single(offenders);
    }

    /// <summary>
    /// A later <c>-filter</c> on a narrower pattern takes the protection away again.
    /// </summary>
    /// <remarks>
    /// gitattributes is last-match-wins per attribute. The preflight used to keep only the
    /// rules that said <c>filter=lfs</c> and ask whether <i>any</i> of them matched, so
    /// <c>*.bin filter=lfs</c> followed by <c>huge.bin -filter</c> still counted as
    /// protected and the warning was suppressed for the one file git would actually commit
    /// in full — a false negative, in a class whose remarks claimed only spurious questions
    /// were possible. The 2026-10-24 review's item 13.
    /// </remarks>
    [Fact]
    public void A_later_unset_filter_takes_the_protection_away()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb), ("other.bin", 20 * Mb)],
            "*.bin filter=lfs diff=lfs merge=lfs -text\nhuge.bin -filter\n");

        Assert.Equal("huge.bin", Assert.Single(offenders));
    }

    [Fact]
    public void A_later_unspecified_filter_takes_the_protection_away()
    {
        // "!filter" is the third spelling: the attribute reverts to unspecified, which for
        // LFS means the same as unset — git commits the content.
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)],
            "*.bin filter=lfs\nhuge.bin !filter\n");

        Assert.Equal("huge.bin", Assert.Single(offenders));
    }

    [Fact]
    public void A_later_different_filter_takes_the_protection_away()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)],
            "*.bin filter=lfs\nhuge.bin filter=clean-only\n");

        Assert.Equal("huge.bin", Assert.Single(offenders));
    }

    [Fact]
    public void A_later_lfs_rule_puts_the_protection_back()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)],
            "*.bin filter=lfs\nhuge.bin -filter\nhuge.bin filter=lfs\n");

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_later_rule_that_says_nothing_about_filter_leaves_the_protection_alone()
    {
        // Under git a rule only changes the attributes it names. Recording every later
        // matching rule as "not lfs" would have traded the false negative for a false
        // positive on the commonest hand-written layout: one line for lfs, one for -diff.
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)],
            "*.bin filter=lfs\n*.bin -diff -merge\n");

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_quoted_pattern_with_a_space_is_read_as_one_pattern()
    {
        // git quotes a pattern that contains whitespace, and so does GitAttributes.Plan
        // since the 2026-10-24 review's item 26; the first-whitespace split read only
        // `"src/My` and matched nothing, so the project's own rule did not count.
        var offenders = LargeFilePreflight.Unprotected(
            [("src/My App/Machine.project", 20 * Mb)],
            "\"src/My App/*.project\" filter=lfs diff=lfs -text\n");

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_filter_whose_name_merely_starts_with_lfs_is_not_protection()
    {
        // The old test was a substring search, so "filter=lfsx" counted.
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)],
            "*.bin filter=lfsx\n");

        Assert.Equal("huge.bin", Assert.Single(offenders));
    }

    /// <summary>
    /// A later bare <c>filter</c> sets the attribute to true, which is not lfs.
    /// </summary>
    /// <remarks>
    /// `git check-attr filter -- huge.bin` answers <c>set</c> for <c>*.bin filter=lfs</c>
    /// followed by <c>huge.bin filter</c> (git 2.55, 2026-10-25): the boolean overrides
    /// the value, and there is no filter driver called "true", so git commits the content.
    /// The token reader knew <c>-filter</c>, <c>!filter</c> and <c>filter=…</c> and let the
    /// fourth spelling fall through as "says nothing about filter", so the earlier lfs rule
    /// stood and the warning was suppressed. The 2026-10-25 review's item 5.
    /// </remarks>
    [Fact]
    public void A_later_boolean_filter_takes_the_protection_away()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb), ("other.bin", 20 * Mb)],
            "*.bin filter=lfs\nhuge.bin filter\n");

        Assert.Equal("huge.bin", Assert.Single(offenders));
    }

    [Fact]
    public void A_boolean_filter_on_the_same_line_wins_over_an_earlier_value()
    {
        // Within one line git takes the last mention: `filter=lfs filter` is `set`, and
        // `filter filter=lfs` is `lfs`. Both asked of git 2.55.
        Assert.Single(LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)], "*.bin filter=lfs filter\n"));
        Assert.Empty(LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)], "*.bin filter filter=lfs\n"));
    }

    /// <summary>
    /// A character class in a later rule is matched, so its <c>-filter</c> counts.
    /// </summary>
    /// <remarks>
    /// <c>huge[.]bin -filter</c> after <c>*.bin filter=lfs</c> leaves git answering
    /// <c>unset</c> for huge.bin. The matcher escaped <c>[</c> and <c>]</c> as literals, the
    /// override matched nothing, and the lfs rule stood — the same quiet false negative as
    /// the boolean, reached through the pattern instead of the attribute. The 2026-10-25
    /// review's item 5.
    /// </remarks>
    [Fact]
    public void A_character_class_in_a_later_rule_takes_the_protection_away()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb), ("other.bin", 20 * Mb)],
            "*.bin filter=lfs\nhuge[.]bin -filter\n");

        Assert.Equal("huge.bin", Assert.Single(offenders));
    }

    /// <summary>
    /// The pattern matcher agrees with git on each shape gitattributes(5) allows.
    /// </summary>
    /// <remarks>
    /// Every row is `git check-attr filter -- &lt;path&gt;` against a one-line file
    /// `&lt;pattern&gt; filter=lfs`, run on git 2.55.0.windows.5 with core.ignorecase on,
    /// on 2026-10-25 — the expectations are git's, not a reading of the manual. The rows
    /// that matter most are the ones where the old matcher said "protected" and git says
    /// "unspecified": a rule read wider than git reads it silences the warning for a file
    /// git will commit in full.
    /// </remarks>
    [Theory]
    // Character classes: ranges, both negation spellings, a leading ] as a member, a
    // trailing - as a member, a POSIX class, and a backslash escape inside the class.
    [InlineData("v[0-9].bin", "v1.bin", true)]
    [InlineData("v[0-9].bin", "va.bin", false)]
    [InlineData("v[0-9].bin", "v10.bin", false)]
    [InlineData("v[!0-9].bin", "v1.bin", false)]
    [InlineData("v[!0-9].bin", "va.bin", true)]
    [InlineData("v[^0-9].bin", "va.bin", true)]
    [InlineData("v[]].bin", "v].bin", true)]
    [InlineData("v[!]a].bin", "v].bin", false)]
    [InlineData("v[!]a].bin", "vb.bin", true)]
    [InlineData("v[a-].bin", "v-.bin", true)]
    [InlineData("v[a-].bin", "vb.bin", false)]
    [InlineData("[[:digit:]].bin", "1.bin", true)]
    [InlineData("[[:digit:]].bin", "a.bin", false)]
    [InlineData("v[\\]].bin", "v].bin", true)]
    [InlineData("v[*].bin", "v*.bin", true)]
    [InlineData("v[*].bin", "va.bin", false)]
    [InlineData("v[a-c].bin", "vB.bin", true)]
    // A class never matches a slash, even when the slash is listed or inside a range.
    [InlineData("a[/]b.bin", "a/b.bin", false)]
    [InlineData("a[+-9]b.bin", "a/b.bin", false)]
    [InlineData("a[+-9]b.bin", "a1b.bin", true)]
    // A class that never closes is a pattern that never matches, not a literal bracket.
    [InlineData("huge[.bin", "huge[.bin", false)]
    [InlineData("huge[.bin", "huge.bin", false)]
    [InlineData("huge.bin[", "huge.bin[", false)]
    // A backslash escapes the next character; one at the end matches nothing.
    [InlineData("huge\\*.bin", "huge*.bin", true)]
    [InlineData("huge\\*.bin", "hugeX.bin", false)]
    [InlineData("huge.bin\\", "huge.bin", false)]
    // ? is one character and not a slash.
    [InlineData("hug?.bin", "huge.bin", true)]
    [InlineData("hug?.bin", "hug.bin", false)]
    [InlineData("hug?.bin", "hug/.bin", false)]
    // Two stars are "any depth" only between slashes or ends; elsewhere they are one star.
    [InlineData("**/huge.bin", "huge.bin", true)]
    [InlineData("**/huge.bin", "a/b/huge.bin", true)]
    [InlineData("a/**/huge.bin", "a/huge.bin", true)]
    [InlineData("a/**/huge.bin", "a/x/y/huge.bin", true)]
    [InlineData("a/**", "a/b/huge.bin", true)]
    [InlineData("a**b.bin", "axb.bin", true)]
    [InlineData("a**b.bin", "a/b.bin", false)]
    [InlineData("a**b.bin", "a/x/b.bin", false)]
    [InlineData("a/**b.bin", "a/xb.bin", true)]
    [InlineData("a/**b.bin", "a/x/b.bin", false)]
    [InlineData("**a/huge.bin", "xa/huge.bin", true)]
    [InlineData("**a/huge.bin", "x/a/huge.bin", false)]
    [InlineData("a/***.bin", "a/huge.bin", true)]
    [InlineData("a/***.bin", "a/x/huge.bin", false)]
    // Anchoring: no slash means the name anywhere; any slash means from the file's folder.
    [InlineData("huge.bin", "a/b/huge.bin", true)]
    [InlineData("a/huge.bin", "x/a/huge.bin", false)]
    [InlineData("/huge.bin", "huge.bin", true)]
    [InlineData("/huge.bin", "a/huge.bin", false)]
    [InlineData("/[hH]uge.bin", "huge.bin", true)]
    [InlineData("/[hH]uge.bin", "a/huge.bin", false)]
    [InlineData("a/", "a/huge.bin", false)]
    public void The_matcher_answers_as_git_does(string pattern, string path, bool protectedByGit)
    {
        var offenders = LargeFilePreflight.Unprotected(
            [(path, 20 * Mb)], pattern + " filter=lfs\n");

        if (protectedByGit)
        {
            Assert.Empty(offenders);
        }
        else
        {
            Assert.Equal(path, Assert.Single(offenders));
        }
    }

    /// <summary>
    /// A quoted pattern with a C-style octal escape names the file git says it names.
    /// </summary>
    /// <remarks>
    /// <c>"huge\056bin" -filter</c> is git's spelling of <c>huge.bin -filter</c>, and
    /// git answers <c>unset</c> for huge.bin. The reader dropped the backslash and kept
    /// the digits, so the override was a rule for a file called huge056bin and the lfs
    /// rule stood. The 2026-10-25 review's item 6, seen from the preflight.
    /// </remarks>
    [Fact]
    public void A_quoted_octal_escape_in_a_later_rule_takes_the_protection_away()
    {
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb), ("other.bin", 20 * Mb)],
            "*.bin filter=lfs\n\"huge\\056bin\" -filter\n");

        Assert.Equal("huge.bin", Assert.Single(offenders));
    }

    [Fact]
    public void A_later_rule_git_can_never_match_changes_nothing()
    {
        // `huge[.bin -filter` is a pattern wildmatch gives up on, so under git the line
        // matches nothing and the earlier lfs rule stands (check-attr: lfs). Dropping the
        // line is right; reading the bracket as a literal, or the whole line as an
        // override, would both disagree with git.
        var offenders = LargeFilePreflight.Unprotected(
            [("huge.bin", 20 * Mb)],
            "*.bin filter=lfs\nhuge[.bin -filter\n");

        Assert.Empty(offenders);
    }
}
