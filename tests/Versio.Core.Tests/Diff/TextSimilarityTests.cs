using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// The score the import planner's last matching pass thresholds on.
/// </summary>
/// <remarks>
/// It decides whether a departed file and an arrived one are the same object renamed, so what
/// matters is the ordering it produces rather than any particular value: a real rename must
/// score well above two unrelated ST files, which share their keyword lines and little else.
/// </remarks>
public class TextSimilarityTests
{
    [Fact]
    public void Identical_text_scores_one_and_unrelated_text_scores_low()
    {
        var pou = "PROGRAM A\nVAR\n  x: INT;\nEND_VAR\nx := x + 1;\nIF x > 10 THEN\n  x := 0;\nEND_IF";

        Assert.Equal(1.0, TextSimilarity.Of(pou, pou));
        Assert.True(TextSimilarity.Of(pou, "PROGRAM B\nVAR\nEND_VAR\nDoSomethingElse();") < 0.6,
            "two unrelated POUs scored above the planner's floor");
    }

    [Fact]
    public void A_renamed_and_lightly_edited_POU_still_scores_well_above_the_floor()
    {
        // The case the pass exists for: the declaration's name changed and one line moved.
        // This must stay clearly above 0.6 or a real rename becomes a delete and a create.
        var before = "PROGRAM Old\nVAR\n  x: INT;\nEND_VAR\nx := x + 1;\nIF x > 10 THEN\n  x := 0;\nEND_IF";
        var after = "PROGRAM New\nVAR\n  x: INT;\nEND_VAR\nx := x + 2;\nIF x > 10 THEN\n  x := 0;\nEND_IF";

        Assert.True(TextSimilarity.Of(before, after) > 0.6,
            $"a renamed POU scored {TextSimilarity.Of(before, after):P0}, below the floor");
    }

    [Fact]
    public void Whitespace_and_blank_lines_do_not_count_as_a_difference()
    {
        // Re-indenting a block is not evidence that a file became a different object.
        Assert.Equal(1.0, TextSimilarity.Of("a\nb\nc", "  a  \n\n\tb\n\nc\n"));
    }

    [Fact]
    public void Lines_count_with_their_multiplicity_rather_than_as_a_set()
    {
        // ST repeats END_IF and END_VAR constantly. As a SET, these two would score 1 — both
        // contain exactly {x, END_IF} — and two very different files would look identical.
        var few = "x;\nEND_IF";
        var many = "x;\nEND_IF\nEND_IF\nEND_IF\nEND_IF\nEND_IF\nEND_IF\nEND_IF";

        Assert.True(TextSimilarity.Of(few, many) < 0.5,
            "a set-based score let a short file match a long one built of repeats");
    }

    [Fact]
    public void A_file_that_merely_contains_another_does_not_score_one()
    {
        // Scored over the LARGER side. Over the smaller, a prefix would score 1 and a rename
        // would pair an object with something that merely swallowed it.
        Assert.True(TextSimilarity.Of("a\nb", "a\nb\nc\nd\ne\nf") < 0.6);
    }

    [Fact]
    public void Two_empty_texts_score_zero_because_the_arithmetic_answer_is_useless()
    {
        // Mathematically they are identical. But an empty file is similar to every other empty
        // file, so returning 1 would let the threshold pair any two of them — and the caller is
        // asking "is there evidence these are the same object", to which there is none.
        Assert.Equal(0, TextSimilarity.Of(string.Empty, string.Empty));
        Assert.Equal(0, TextSimilarity.Of(null, "a"));
        Assert.Equal(0, TextSimilarity.Of("   \n\n", "a"));
    }
}
