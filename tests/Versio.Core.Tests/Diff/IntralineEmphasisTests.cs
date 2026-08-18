using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// Which characters of a changed pair light up — the "here" on top of the row tint's "this
/// line changed".
/// </summary>
/// <remarks>
/// The inputs that matter are the corpus's own shape: generated XML and ST where a long line
/// differs by one identifier or one attribute value. Every case here is that shape or one of
/// its edges.
/// </remarks>
public sealed class IntralineEmphasisTests
{
    private static (TextSpan? Old, TextSpan? New) Compare(string oldText, string newText) =>
        IntralineEmphasis.Compare(oldText, newText);

    private static string Marked(string text, TextSpan? span) =>
        span is { } s ? text.Substring(s.Start, s.Length) : string.Empty;

    [Fact]
    public void One_identifier_changed_in_a_long_line_is_exactly_what_lights_up()
    {
        const string oldText = "<attribute name=\"Speed\" value=\"rpmMotorOld\" scope=\"local\" />";
        const string newText = "<attribute name=\"Speed\" value=\"rpmMotorNew\" scope=\"local\" />";

        var (old, @new) = Compare(oldText, newText);

        Assert.Equal("rpmMotorOld", Marked(oldText, old));
        Assert.Equal("rpmMotorNew", Marked(newText, @new));
    }

    [Fact]
    public void A_changed_digit_emphasises_the_whole_identifier()
    {
        // "Counter1" → "Counter2" shares "Counter" character-wise, and emphasising only the
        // digit invites reading half an identifier as the change — half a name is not a
        // shorter version of it.
        var (old, @new) = Compare("iValue := Counter1;", "iValue := Counter2;");

        Assert.Equal("Counter1", Marked("iValue := Counter1;", old));
        Assert.Equal("Counter2", Marked("iValue := Counter2;", @new));
    }

    [Fact]
    public void A_pure_insertion_lights_only_the_side_that_gained_it()
    {
        var (old, @new) = Compare("VAR speed : INT; END_VAR", "VAR speed : INT := 5; END_VAR");

        Assert.Null(old);
        Assert.Equal(" := 5", Marked("VAR speed : INT := 5; END_VAR", @new));
    }

    [Fact]
    public void Nothing_in_common_means_no_emphasis()
    {
        // Emphasising every character of both rows says less than the row tint already does.
        var (old, @new) = Compare("completely different", "XYZQW 123");

        Assert.Null(old);
        Assert.Null(@new);
    }

    [Fact]
    public void Identical_lines_are_no_ones_business()
    {
        var (old, @new) = Compare("same", "same");

        Assert.Null(old);
        Assert.Null(@new);
    }

    [Fact]
    public void The_document_pass_pairs_the_ith_removed_with_the_ith_added()
    {
        var document = IntralineEmphasis.Annotate(UnifiedDiffParser.Parse(
            "@@ -1,2 +1,2 @@\n" +
            "-iSpeed := 100;\n" +
            "-iRamp := 5;\n" +
            "+iSpeed := 200;\n" +
            "+iRamp := 9;\n"));

        var removed = document.Lines.Where(l => l.Kind == DiffLineKind.Removed).ToList();
        var added = document.Lines.Where(l => l.Kind == DiffLineKind.Added).ToList();

        Assert.Equal("100", Marked(removed[0].Text, removed[0].Emphasis.Single()));
        Assert.Equal("200", Marked(added[0].Text, added[0].Emphasis.Single()));
        Assert.Equal("5", Marked(removed[1].Text, removed[1].Emphasis.Single()));
        Assert.Equal("9", Marked(added[1].Text, added[1].Emphasis.Single()));
    }

    [Fact]
    public void An_unbalanced_blocks_tail_is_left_alone()
    {
        // The third added line has no removed counterpart — it is a new line, not an edit,
        // and emphasis against an imaginary partner would be an invented comparison.
        var document = IntralineEmphasis.Annotate(UnifiedDiffParser.Parse(
            "@@ -1,1 +1,2 @@\n" +
            "-iSpeed := 100;\n" +
            "+iSpeed := 200;\n" +
            "+iBrandNew := 1;\n"));

        var added = document.Lines.Where(l => l.Kind == DiffLineKind.Added).ToList();

        Assert.NotEmpty(added[0].Emphasis);
        Assert.Empty(added[1].Emphasis);
    }

    [Fact]
    public void Context_lines_never_carry_emphasis()
    {
        var document = IntralineEmphasis.Annotate(UnifiedDiffParser.Parse(
            "@@ -1,3 +1,3 @@\n" +
            " unchanged\n" +
            "-old\n" +
            "+new\n" +
            " also unchanged\n"));

        Assert.All(document.Lines.Where(l => l.Kind == DiffLineKind.Context),
            l => Assert.Empty(l.Emphasis));
    }
}
