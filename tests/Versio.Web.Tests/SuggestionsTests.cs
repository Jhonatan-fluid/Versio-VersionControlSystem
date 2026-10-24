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

    [Fact]
    public void Applying_replaces_the_lines_and_keeps_the_files_line_endings_and_its_final_newline()
    {
        Assert.Equal("PROGRAM Pump\nVAR\n  run : BOOL := TRUE;\nEND_VAR\n", Suggestions.Apply("PROGRAM Pump\nVAR\n  run : BOOL;\nEND_VAR\n", 3, 3, "  run : BOOL := TRUE;\n"));
        Assert.Equal("PROGRAM Pump\r\nVAR\r\n  run : BOOL := TRUE;\r\n  ok : BOOL;\r\nEND_VAR\r\n", Suggestions.Apply("PROGRAM Pump\r\nVAR\r\n  run : BOOL;\r\nEND_VAR\r\n", 3, 3, "  run : BOOL := TRUE;\n  ok : BOOL;\n"));
        // A range, replaced by fewer lines; a file with no final newline stays without one.
        Assert.Equal("a\nX\nd", Suggestions.Apply("a\nb\nc\nd", 2, 3, "X"));
        // An empty suggestion deletes the lines, as on github.com.
        Assert.Equal("a\nd\n", Suggestions.Apply("a\nb\nc\nd\n", 2, 3, ""));
    }

    [Fact]
    public void Lines_outside_the_file_or_a_backwards_range_apply_nothing()
    {
        Assert.Null(Suggestions.Apply("a\nb\n", 3, 3, "x"));
        Assert.Null(Suggestions.Apply("a\nb\n", 0, 1, "x"));
        Assert.Null(Suggestions.Apply("a\nb\n", 2, 1, "x"));
    }
}
