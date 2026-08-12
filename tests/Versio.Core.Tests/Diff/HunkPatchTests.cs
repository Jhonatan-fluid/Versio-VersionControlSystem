using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// Extracting one hunk of a diff as a patch git will apply.
/// </summary>
/// <remarks>
/// <para>
/// The whole risk here is producing something that <i>looks</i> like a patch and is not.
/// <c>git apply</c> checks a hunk header against its body and refuses the lot on a mismatch,
/// so the tests below are mostly about what is copied and what is left alone.
/// </para>
/// <para>
/// Byte fidelity is asserted rather than shape: the point of copying the hunk verbatim is that
/// nothing in the middle reformats it, and a test that only checked "contains the added line"
/// would pass against a builder that had quietly rewritten the header.
/// </para>
/// </remarks>
public sealed class HunkPatchTests
{
    private const string TwoHunks =
        "diff --git a/Simulation.st b/Simulation.st\n" +
        "index 1a2b3c4..5d6e7f8 100644\n" +
        "--- a/Simulation.st\n" +
        "+++ b/Simulation.st\n" +
        "@@ -10,6 +10,7 @@ PROGRAM Simulation\n" +
        " VAR\n" +
        "     rTemp : REAL;\n" +
        "+    rSetpoint : REAL;\n" +
        " END_VAR\n" +
        " \n" +
        " rTemp := 4.0;\n" +
        "@@ -40,7 +41,7 @@ END_VAR\n" +
        " IF bRun THEN\n" +
        "-    rTemp := rTemp - 0.1;\n" +
        "+    rTemp := rTemp - 0.2;\n" +
        " END_IF\n";

    [Fact]
    public void The_first_hunk_comes_back_with_its_file_header_and_nothing_from_the_second()
    {
        var patch = HunkPatchBuilder.Build(TwoHunks, 0);

        Assert.Equal(
            "diff --git a/Simulation.st b/Simulation.st\n" +
            "index 1a2b3c4..5d6e7f8 100644\n" +
            "--- a/Simulation.st\n" +
            "+++ b/Simulation.st\n" +
            "@@ -10,6 +10,7 @@ PROGRAM Simulation\n" +
            " VAR\n" +
            "     rTemp : REAL;\n" +
            "+    rSetpoint : REAL;\n" +
            " END_VAR\n" +
            " \n" +
            " rTemp := 4.0;\n",
            patch);
    }

    [Fact]
    public void The_second_hunk_comes_back_with_the_same_header()
    {
        // A patch without a file header applies to nothing, so every hunk carries one -
        // including the ones that appeared halfway down the diff.
        var patch = HunkPatchBuilder.Build(TwoHunks, 1);

        Assert.StartsWith("diff --git a/Simulation.st b/Simulation.st\n", patch);
        Assert.Contains("@@ -40,7 +41,7 @@ END_VAR\n", patch);
        Assert.DoesNotContain("rSetpoint", patch);
    }

    [Fact]
    public void The_hunk_header_is_copied_exactly_including_its_trailing_context()
    {
        // git checks the counts in the header against the body and refuses the whole patch on
        // a mismatch. Recomputing them would work until it did not, and the failure would be
        // git's own wording about a patch the user never wrote.
        var patch = HunkPatchBuilder.Build(TwoHunks, 0);

        Assert.Contains("@@ -10,6 +10,7 @@ PROGRAM Simulation\n", patch);
    }

    [Fact]
    public void An_index_out_of_range_yields_nothing_rather_than_a_wrong_patch()
    {
        Assert.Null(HunkPatchBuilder.Build(TwoHunks, 2));
        Assert.Null(HunkPatchBuilder.Build(TwoHunks, -1));
        Assert.Null(HunkPatchBuilder.Build(null, 0));
        Assert.Null(HunkPatchBuilder.Build(string.Empty, 0));
    }

    [Fact]
    public void A_diff_with_no_hunks_at_all_yields_nothing()
    {
        const string binary =
            "diff --git a/Machine.project b/Machine.project\n" +
            "index 1a2b3c4..5d6e7f8 100644\n" +
            "Binary files a/Machine.project and b/Machine.project differ\n";

        Assert.Null(HunkPatchBuilder.Build(binary, 0));
        Assert.Equal(0, HunkPatchBuilder.Count(binary));
    }

    [Fact]
    public void A_hunk_from_a_multi_file_diff_carries_its_own_files_header()
    {
        // What `git show` produces for a commit. Carrying the first file's header would
        // produce a patch that applied the second file's changes to the first - which is a
        // patch git would accept, and the worst possible outcome here.
        const string twoFiles =
            "diff --git a/first.st b/first.st\n" +
            "index aaa..bbb 100644\n" +
            "--- a/first.st\n" +
            "+++ b/first.st\n" +
            "@@ -1,2 +1,3 @@\n" +
            " one\n" +
            "+first added\n" +
            " two\n" +
            "diff --git a/second.st b/second.st\n" +
            "index ccc..ddd 100644\n" +
            "--- a/second.st\n" +
            "+++ b/second.st\n" +
            "@@ -5,2 +5,3 @@\n" +
            " five\n" +
            "+second added\n" +
            " six\n";

        var patch = HunkPatchBuilder.Build(twoFiles, 1);

        Assert.StartsWith("diff --git a/second.st b/second.st\n", patch);
        Assert.Contains("+++ b/second.st\n", patch);
        Assert.Contains("+second added\n", patch);
        Assert.DoesNotContain("first.st", patch);
        Assert.DoesNotContain("first added", patch);
    }

    [Fact]
    public void A_new_file_keeps_the_mode_line_that_says_it_is_new()
    {
        const string added =
            "diff --git a/notes.md b/notes.md\n" +
            "new file mode 100644\n" +
            "index 0000000..1a2b3c4\n" +
            "--- /dev/null\n" +
            "+++ b/notes.md\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+first\n" +
            "+second\n";

        var patch = HunkPatchBuilder.Build(added, 0);

        Assert.Contains("new file mode 100644\n", patch);
        Assert.Contains("--- /dev/null\n", patch);
    }

    [Fact]
    public void A_missing_trailing_newline_marker_stays_with_its_hunk()
    {
        // "\ No newline at end of file" is part of the hunk, not commentary on it. Dropping it
        // changes what the patch means about the last line.
        const string noNewline =
            "diff --git a/a.st b/a.st\n" +
            "index aaa..bbb 100644\n" +
            "--- a/a.st\n" +
            "+++ b/a.st\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "\\ No newline at end of file\n" +
            "+new\n" +
            "\\ No newline at end of file\n";

        var patch = HunkPatchBuilder.Build(noNewline, 0);

        Assert.Equal(2, patch!.Split('\n').Count(l => l.StartsWith('\\')));
    }

    [Fact]
    public void Crlf_line_endings_survive_the_round_trip()
    {
        // git compares what it applies against the file on disk. A patch whose line endings
        // were normalised in passing would not match a CRLF working tree, and the refusal
        // would look like a bug in git.
        const string crlf =
            "diff --git a/a.st b/a.st\r\n" +
            "index aaa..bbb 100644\r\n" +
            "--- a/a.st\r\n" +
            "+++ b/a.st\r\n" +
            "@@ -1 +1 @@\r\n" +
            "-old\r\n" +
            "+new\r\n";

        var patch = HunkPatchBuilder.Build(crlf, 0);

        Assert.Equal(crlf, patch);
    }

    [Fact]
    public void The_hunk_count_matches_what_the_parser_numbered()
    {
        // The view puts a button on a hunk header and passes back the index the parser gave
        // that row. If these two ever counted differently, the button would stage a different
        // hunk than the one it sits on.
        var document = UnifiedDiffParser.Parse(TwoHunks);

        Assert.Equal(2, HunkPatchBuilder.Count(TwoHunks));
        Assert.Equal(2, document.Hunks);

        Assert.Equal([0, 1],
            document.Lines
                .Where(l => l.Kind == DiffLineKind.HunkHeader)
                .Select(l => l.HunkIndex));
    }

    [Fact]
    public void Every_body_line_is_numbered_with_the_hunk_it_sits_in()
    {
        var document = UnifiedDiffParser.Parse(TwoHunks);

        var added = document.Lines.Where(l => l.Kind == DiffLineKind.Added).ToList();
        Assert.Equal(0, added[0].HunkIndex);
        Assert.Equal(1, added[1].HunkIndex);

        // File headers belong to no hunk, and a button must never appear on one.
        Assert.All(document.Lines.Where(l => l.Kind == DiffLineKind.FileHeader),
                   l => Assert.Equal(-1, l.HunkIndex));
    }
}
