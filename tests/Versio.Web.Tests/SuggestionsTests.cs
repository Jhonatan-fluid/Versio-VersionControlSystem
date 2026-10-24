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

    [Fact]
    public void Lines_outside_the_file_or_a_backwards_range_apply_nothing()
    {
        Assert.Null(Suggestions.Apply("a\nb\n", 3, 3, "x"));
        Assert.Null(Suggestions.Apply("a\nb\n", 0, 1, "x"));
        Assert.Null(Suggestions.Apply("a\nb\n", 2, 1, "x"));
    }
}
