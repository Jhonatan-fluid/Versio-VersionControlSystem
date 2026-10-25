using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// A quoted <c>.gitattributes</c> pattern is read back, and written, the way git reads it.
/// </summary>
/// <remarks>
/// git's quoted pattern is C-quoted (<c>unquote_c_style</c>): the named escapes, a
/// verbatim <c>\\</c> and <c>\"</c>, and a three-digit octal byte. Every expectation here
/// was first asked of <c>git check-attr</c> on git 2.55.0.windows.5 (2026-10-25); a
/// decoding git refuses is a line git ignores, and so it is null here.
/// </remarks>
public sealed class GitAttributesPatternTests
{
    /// <summary>
    /// <c>"huge\056bin"</c> is huge.bin, because <c>\056</c> is the octal for a dot.
    /// </summary>
    /// <remarks>
    /// The reader dropped the backslash and kept the digits, so git's own spelling of an
    /// override for huge.bin was read as a rule for a file called huge056bin — and the
    /// large-file preflight kept protecting the file git would commit in full. The
    /// 2026-10-25 review's item 6.
    /// </remarks>
    [Fact]
    public void An_octal_escape_in_a_quoted_pattern_is_decoded()
    {
        var read = GitAttributesPattern.Read("\"huge\\056bin\" -filter");

        Assert.Equal(("huge.bin", 13), read);
    }

    [Theory]
    [InlineData("\"a\\tb\"", "a\tb")]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData("\"a\\rb\"", "a\rb")]
    [InlineData("\"a\\ab\"", "a\ab")]
    [InlineData("\"a\\bb\"", "a\bb")]
    [InlineData("\"a\\fb\"", "a\fb")]
    [InlineData("\"a\\vb\"", "a\vb")]
    [InlineData("\"a\\\\b\"", "a\\b")]
    [InlineData("\"a\\\"b\"", "a\"b")]
    [InlineData("\"\\141.bin\"", "a.bin")]
    [InlineData("\"hug\\145.bin\"", "huge.bin")]
    public void Each_C_escape_decodes_as_git_decodes_it(string line, string pattern)
    {
        var read = GitAttributesPattern.Read(line);

        Assert.Equal((pattern, line.Length), read);
    }

    [Fact]
    public void Octal_bytes_are_one_UTF8_string_not_one_character_each()
    {
        // git quotes a name byte by byte, so ä arrives as its two UTF-8 bytes \303\244.
        // Appending each byte as a char would give two Latin-1 characters and a pattern
        // that matches no file in the repository.
        var read = GitAttributesPattern.Read("\"\\303\\244.bin\" filter=lfs");

        Assert.Equal(("ä.bin", 14), read);
    }

    /// <summary>A line git refuses to decode is a line git ignores, so it is null here.</summary>
    /// <remarks>
    /// git's reader insists on exactly three octal digits with the first at most 3, and on
    /// a letter it knows; anything else makes <c>unquote_c_style</c> fail and the whole
    /// line is dropped — `check-attr` answers <c>unspecified</c> for huge.bin, huge.6bin
    /// and the literal spelling alike. Reading the escape leniently would turn a line git
    /// ignores into a rule, and as a later <c>-filter</c> that would be an override git
    /// does not apply.
    /// </remarks>
    [Theory]
    [InlineData("\"huge\\56bin\" filter=lfs")]
    [InlineData("\"huge\\456bin\" filter=lfs")]
    [InlineData("\"huge\\qbin\" filter=lfs")]
    [InlineData("\"huge\\0\" filter=lfs")]
    [InlineData("\"huge\\")]
    [InlineData("\"huge.bin filter=lfs")]
    public void A_quoting_git_refuses_is_null(string line)
    {
        Assert.Null(GitAttributesPattern.Read(line));
    }

    [Theory]
    [InlineData("plain.project")]
    [InlineData("src/My App/*.project")]
    [InlineData("a \"b\"/export/**")]
    [InlineData("a\\b")]
    [InlineData("tab\there")]
    [InlineData("bell\a.bin")]
    [InlineData("\u0001.bin")]
    [InlineData("ä/export/**")]
    public void What_Quote_writes_Read_reads_back(string pattern)
    {
        var line = GitAttributesPattern.Quote(pattern) + " filter=lfs";

        var (read, end) = Assert.NotNull(GitAttributesPattern.Read(line));
        Assert.Equal(pattern, read);
        Assert.Equal(" filter=lfs", line[end..]);
    }

    [Fact]
    public void A_control_character_is_written_as_git_spells_it()
    {
        // The writer agrees with git's own quote_c_style, not merely with its own reader:
        // a named escape where C has one, three octal digits otherwise. A literal tab
        // inside the quotes would also have read back, but it is invisible in a diff.
        Assert.Equal("\"tab\\there\"", GitAttributesPattern.Quote("tab\there"));
        Assert.Equal("\"bell\\a.bin\"", GitAttributesPattern.Quote("bell\a.bin"));
        Assert.Equal("\"\\001.bin\"", GitAttributesPattern.Quote("\u0001.bin"));
    }

    [Fact]
    public void A_pattern_that_needs_no_quoting_is_left_bare()
    {
        // Byte-identical to what every adoption before quoting existed wrote, so no
        // existing file reads as uncovered — and a non-ASCII name stays readable: git
        // accepts it bare, so there is no reason to write \303\244 for ä.
        Assert.Equal("*.project", GitAttributesPattern.Quote("*.project"));
        Assert.Equal("ä/export/**", GitAttributesPattern.Quote("ä/export/**"));
    }
}
