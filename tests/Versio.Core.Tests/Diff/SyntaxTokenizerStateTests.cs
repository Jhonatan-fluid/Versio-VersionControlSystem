using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// The tokenizer with a state carried between lines (review, 2026-10-24): a block comment's second
/// line is a comment, the line that closes it is a comment up to the close and code after, and a
/// reset is the stateless answer again. The stateless call is unchanged by any of it.
/// </summary>
public sealed class SyntaxTokenizerStateTests
{
    private static readonly SyntaxLanguage St = LanguageCatalog.StructuredText;

    /// <summary>Each token's kind and trimmed text; whitespace-only tokens left out, they are not what these tests are about.</summary>
    private static string Kinds(string line, SyntaxTokenizer.TokenState? state) =>
        string.Join(" ", SyntaxTokenizer.Tokenize(line, St, state)
            .Select(t => (t.Kind, Text: line.Substring(t.Start, t.Length).Trim()))
            .Where(t => t.Text.Length > 0)
            .Select(t => $"{t.Kind}:{t.Text}"));

    [Fact]
    public void A_block_comment_left_open_makes_the_next_lines_comments_until_it_closes()
    {
        var state = new SyntaxTokenizer.TokenState();

        Assert.Equal("Comment:(* start", Kinds("(* start", state));
        Assert.True(state.InComment);
        Assert.Equal("Comment:FB_Pump();", Kinds("\tFB_Pump();", state));
        Assert.True(state.InComment);
        Assert.Equal("Comment:end *) Plain:x Plain::= Number:1 Plain:;", Kinds("end *) x := 1;", state));
        Assert.False(state.InComment);
        Assert.Equal("Plain:x Plain::= Number:2 Plain:;", Kinds("x := 2;", state));
    }

    [Fact]
    public void A_comment_that_closes_on_its_own_line_leaves_nothing_open_and_a_reset_forgets_one()
    {
        var state = new SyntaxTokenizer.TokenState();

        Assert.Equal("Comment:(* one *) Plain:x", Kinds("(* one *) x", state));
        Assert.False(state.InComment);
        Kinds("(* open", state);
        Assert.True(state.InComment);
        state.Reset();
        Assert.Equal("Plain:x", Kinds("x", state));
    }

    [Fact]
    public void Without_a_state_every_line_stands_alone_and_a_line_inside_a_comment_is_never_a_directive()
    {
        Assert.Equal("Plain:FB_Pump Plain:();", Kinds("FB_Pump();", null));
        var state = new SyntaxTokenizer.TokenState();
        Kinds("(* open", state);
        // A pragma-shaped line inside the comment is comment, not a directive claiming the line.
        Assert.Equal("Comment:{attribute 'x'}", Kinds("{attribute 'x'}", state));
        Kinds("*)", state);
        Assert.Equal("Directive:{attribute 'x'}", Kinds("{attribute 'x'}", state));
    }

    [Fact]
    public void The_spans_still_cover_every_line_exactly_once_with_a_state()
    {
        var state = new SyntaxTokenizer.TokenState();
        foreach (var line in new[] { "a := 1; (* open", "still 'in' // it", "close *) b := 'x'; (* again *) c", "d" })
        {
            var spans = SyntaxTokenizer.Tokenize(line, St, state);
            var at = 0;
            foreach (var span in spans)
            {
                Assert.Equal(at, span.Start);
                at += span.Length;
            }

            Assert.Equal(line.Length, at);
        }
    }
}
