using Versio.Web.Review;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Suggested changes, the pure halves (docs/web/FINISH.md, Step 5): what a comment body carries,
/// and what the file becomes — with the file's own line endings kept, because turning one changed
/// line into a whole-file diff is the noise this project exists to avoid.
/// </summary>
public sealed class SuggestionsTests
{
    [Fact]
    public void The_first_suggestion_fence_is_read_and_a_body_without_one_carries_none()
    {
        Assert.Equal("  run : BOOL := TRUE;\n", Suggestions.Read("Use the default.\n\n```suggestion\n  run : BOOL := TRUE;\n```\n\nand then\n```suggestion\nsecond\n```"));
        Assert.Equal("", Suggestions.Read("Drop it.\n```suggestion\n```"));
        Assert.Equal("a\nb\n", Suggestions.Read("```suggestion\r\na\r\nb\r\n```"));
        Assert.Null(Suggestions.Read("```st\n  run : BOOL;\n```"));
        Assert.Null(Suggestions.Read("No code here."));
        Assert.Null(Suggestions.Read(null));
    }

    /// <summary>
    /// A CRLF-formatted comment (a body written in a Windows tool, or pasted through one) is a
    /// suggestion all the same. Its closing fence is followed by "\r\n", and .NET's <c>$</c> under
    /// <c>(?m)</c> matches before '\n' only — so the LF body and the CRLF body with no newline after
    /// the fence were read while this one, the usual CRLF shape, came back as "no suggestion"
    /// (review, 2026-10-25). Read and Without see the same fence either way.
    /// </summary>
    [Fact]
    public void A_crlf_body_whose_closing_fence_ends_the_line_is_read_like_the_lf_one()
    {
        Assert.Equal("a\nb\n", Suggestions.Read("```suggestion\r\na\r\nb\r\n```\r\n"));
        Assert.Equal("  run : BOOL := TRUE;\n", Suggestions.Read("Use the default.\r\n\r\n```suggestion\r\n  run : BOOL := TRUE;\r\n```\r\n\r\nand then"));
        Assert.Equal("", Suggestions.Read("Drop it.\r\n```suggestion\r\n```\r\n"));
        Assert.Equal("x\n\n", Suggestions.Read("```suggestion\r\nx\r\n\r\n```\r\n"));
        // The closing fence's own "\r\n" is the prose's line break, not the fence's: taken out, the
        // body reads as the LF one does, with its own endings kept.
        Assert.Equal("Use the default.", Suggestions.Without("Use the default.\r\n\r\n```suggestion\r\n  run : BOOL := TRUE;\r\n```\r\n"));
        Assert.Equal("Before\r\n\r\nAfter", Suggestions.Without("Before\r\n```suggestion\r\nx\r\n```\r\nAfter"));
    }

    /// <summary>The prose is drawn without the fence, which the page draws as the widget: shown once, as on github.com.</summary>
    [Fact]
    public void The_body_without_its_suggestion_is_the_prose_alone()
    {
        Assert.Equal("Use the default.", Suggestions.Without("Use the default.\n\n```suggestion\n  run : BOOL := TRUE;\n```\n"));
        Assert.Equal("Before\n\nAfter", Suggestions.Without("Before\n```suggestion\nx\n```\nAfter"));
        Assert.Equal("No code here.", Suggestions.Without("No code here."));
    }

    [Fact]
    public void Applying_replaces_the_lines_and_keeps_the_files_line_endings_and_its_final_newline()
    {
        Assert.Equal("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\nEND_VAR\n", Suggestions.Apply("PROGRAM Pump\nVAR\n  run : BOOL;\nEND_VAR\n", 3, 3, "  run : BOOL := TRUE;\n"));
        Assert.Equal("PROGRAM Pump\r\nVAR\r\n  run : BOOL := TRUE;\r\n  ok : BOOL;\r\nEND_VAR\r\n", Suggestions.Apply("PROGRAM Pump\r\nVAR\r\n  run : BOOL;\r\nEND_VAR\r\n", 3, 3, "  run : BOOL := TRUE;\n  ok : BOOL;\n"));
        // A range, replaced by fewer lines; a file with no final newline stays without one.
        Assert.Equal("a\nX\nd", Suggestions.Apply("a\nb\nc\nd", 2, 3, "X"));
        // An empty suggestion deletes the lines, as on github.com.
        Assert.Equal("a\nd\n", Suggestions.Apply("a\nb\nc\nd\n", 2, 3, ""));
        // A blank line before the closing fence is a line (github.com keeps it); only the newline
        // that ends the fence's body is not (review, 2026-10-24).
        Assert.Equal("x\n\n", Suggestions.Read("```suggestion\nx\n\n```"));
        Assert.Equal("a\nx\n\nc\n", Suggestions.Apply("a\nb\nc\n", 2, 2, "x\n\n"));
        Assert.Equal("a\nx\nc\n", Suggestions.Apply("a\nb\nc\n", 2, 2, "x"));
    }

    /// <summary>
    /// Only the selected lines change. A file with mixed endings (a CRLF export with one LF line
    /// pasted in, or the other way round) used to come back rebuilt with one ending throughout,
    /// so the commit changed lines the reviewer never selected — the whole-file noise this class
    /// says it exists to avoid; and a lone <c>\r</c> that was content, not an ending, was trimmed
    /// off with it (review, 2026-10-25).
    /// </summary>
    [Fact]
    public void Applying_leaves_every_unselected_line_byte_for_byte_as_it_was()
    {
        // Mixed endings: line 2 is replaced, lines 1 and 3 keep their own.
        Assert.Equal("a\r\nX\nc\r\n", Suggestions.Apply("a\r\nb\nc\r\n", 2, 2, "X\n"));
        Assert.Equal("a\nX\r\nc\n", Suggestions.Apply("a\nb\r\nc\n", 2, 2, "X\n"));
        // A '\r' before the ending is content, as is one ending a final line with no newline.
        Assert.Equal("a\r\r\nX\r\n", Suggestions.Apply("a\r\r\nb\r\n", 2, 2, "X\n"));
        Assert.Equal("X\nb\r", Suggestions.Apply("a\nb\r", 1, 1, "X\n"));
        // A lone CR in the middle of a line is content too, on both sides of the selection.
        Assert.Equal("a\rb\nX\nc\rd\n", Suggestions.Apply("a\rb\nY\nc\rd\n", 2, 2, "X\n"));
    }

    /// <summary>
    /// The lines a suggestion inserts take the ending of the first line they replace, since a fence
    /// typed on github.com says nothing about endings; a replaced final line with no newline keeps
    /// having none, and the file's dominant ending decides when that first line had no ending of its own.
    /// </summary>
    [Fact]
    public void Inserted_lines_take_the_ending_of_the_first_line_they_replace()
    {
        Assert.Equal("a\nx\r\ny\r\nc\n", Suggestions.Apply("a\nb\r\nc\n", 2, 2, "x\ny\n"));
        Assert.Equal("a\r\nx\ny\nd\r\n", Suggestions.Apply("a\r\nb\nc\r\nd\r\n", 2, 3, "x\ny\n"));
        // The final line had no newline: the first inserted line takes the dominant ending, the last stays bare.
        Assert.Equal("a\r\nb\r\nx\r\ny", Suggestions.Apply("a\r\nb\r\nc", 3, 3, "x\ny\n"));
        Assert.Equal("a\nb\r\nc\nx\ny", Suggestions.Apply("a\nb\r\nc\nd", 4, 4, "x\ny\n"));
        // A tie goes to CRLF, as the first version chose whenever one was present at all.
        Assert.Equal("a\r\nb\nx\r\ny", Suggestions.Apply("a\r\nb\nc", 3, 3, "x\ny\n"));
        // A final line that did end with a newline is replaced by one that does.
        Assert.Equal("a\nx\n", Suggestions.Apply("a\nb\n", 2, 2, "x\n"));
        Assert.Equal("a\r\nx\r\ny\r\n", Suggestions.Apply("a\r\nb\r\n", 2, 2, "x\ny\n"));
        // An empty file is one bare line; with nothing to copy, the inserted lines take '\n'.
        Assert.Equal("x\ny", Suggestions.Apply("", 1, 1, "x\ny\n"));
        // Deleting the final bare line leaves the line before it with the ending it already had.
        Assert.Equal("a\nb\n", Suggestions.Apply("a\nb\nc", 3, 3, ""));
    }

    [Fact]
    public void Lines_outside_the_file_or_a_backwards_range_apply_nothing()
    {
        Assert.Null(Suggestions.Apply("a\nb\n", 3, 3, "x"));
        Assert.Null(Suggestions.Apply("a\nb\n", 0, 1, "x"));
        Assert.Null(Suggestions.Apply("a\nb\n", 2, 1, "x"));
    }
}
