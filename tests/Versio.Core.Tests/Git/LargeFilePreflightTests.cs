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
}
