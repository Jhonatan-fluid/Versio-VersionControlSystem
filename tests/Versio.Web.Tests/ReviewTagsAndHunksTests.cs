using Versio.Web.Review;
using Versio.Web.Review.GitHub;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>The operation tags and keys (C5), and the lines GitHub will take a comment on (C6).</summary>
public sealed class ReviewTagsAndHunksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static string KeyAt(DateTimeOffset at) => $"{at.ToUnixTimeMilliseconds()}-abcdefghijklmnop1234";

    [Fact]
    public void A_key_carries_its_own_age_so_expiry_needs_no_storage()
    {
        Assert.Equal(KeyCheck.Ok, ReviewTags.CheckKey(KeyAt(Now.AddDays(-6)), Now));
        Assert.Equal(KeyCheck.Expired, ReviewTags.CheckKey(KeyAt(Now.AddDays(-8)), Now));
        Assert.Equal(KeyCheck.Malformed, ReviewTags.CheckKey(KeyAt(Now.AddHours(1)), Now));
        Assert.Equal(KeyCheck.Malformed, ReviewTags.CheckKey("1-x", Now));
        Assert.Equal(KeyCheck.Malformed, ReviewTags.CheckKey(null, Now));
    }

    [Fact]
    public void A_posted_body_carries_its_tag_on_its_own_line_and_drawing_strips_every_versio_tag()
    {
        var key = KeyAt(Now);
        var posted = ReviewTags.WithOperation("Why **TRUE**?", key);

        Assert.True(ReviewTags.Carries(posted, key));
        Assert.EndsWith("\n\n" + ReviewTags.OperationTag(key), posted, StringComparison.Ordinal);
        Assert.Equal("Why **TRUE**?", ReviewTags.Strip(posted));
        // A forged tag typed on github.com, anywhere in a body, never reaches a reader.
        Assert.Equal("before  after", ReviewTags.Strip("before <!-- versio:anchor v1 {\"kind\":\"net\"} --> after"));
        // Editing an already-tagged body replaces the tag rather than stacking them.
        var edited = ReviewTags.WithOperation(posted, KeyAt(Now.AddMinutes(1)));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(edited, "versio:op"));
    }

    [Fact]
    public void Context_lines_take_comments_on_both_sides_and_changed_lines_only_on_theirs()
    {
        var lines = HunkLines.Parse("@@ -3,3 +3,3 @@\n keep\n-old\n+new\n same");

        Assert.True(lines.Accepts("RIGHT", 3));
        Assert.True(lines.Accepts("LEFT", 3));
        Assert.True(lines.Accepts("RIGHT", 4));
        Assert.True(lines.Accepts("LEFT", 4));
        Assert.True(lines.Accepts("RIGHT", 5));
        Assert.False(lines.Accepts("RIGHT", 6));
        Assert.False(lines.Accepts("RIGHT", 2));
    }

    [Fact]
    public void A_range_must_stay_inside_one_hunk_and_no_newline_markers_are_not_lines()
    {
        var lines = HunkLines.Parse("@@ -1,2 +1,2 @@\n a\n-b\n+B\n\\ No newline at end of file\n@@ -10,2 +10,2 @@\n x\n-y\n+Y");

        Assert.True(lines.Accepts("RIGHT", 2, start: 1));
        Assert.False(lines.Accepts("RIGHT", 10, start: 2));
        Assert.True(lines.Accepts("RIGHT", 11, start: 10));
        Assert.False(lines.Accepts("RIGHT", 3));
    }
}
