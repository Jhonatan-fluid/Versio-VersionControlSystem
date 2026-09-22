using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

public class UnifiedDiffParserTests
{
    private const string Sample = """
        diff --git a/plc/Main.st b/plc/Main.st
        index 9291d00..a1b2c3d 100644
        --- a/plc/Main.st
        +++ b/plc/Main.st
        @@ -10,7 +10,8 @@ PROGRAM Main
         VAR
             bStart : BOOL;
        -    nSpeed : INT := 100;
        +    nSpeed : INT := 200;
        +    nAccel : INT := 5;
         END_VAR
        """;

    [Fact]
    public void Added_and_removed_lines_are_classified_and_counted()
    {
        var doc = UnifiedDiffParser.Parse(Sample);

        Assert.Equal(2, doc.Added);
        Assert.Equal(1, doc.Removed);
        Assert.Equal("+2 −1", doc.Summary);
    }

    [Fact]
    public void The_leading_marker_is_stripped_from_the_text()
    {
        // The marker belongs in the gutter, not in the code - otherwise every line is
        // shifted one column and nothing lines up.
        var doc = UnifiedDiffParser.Parse(Sample);

        var added = doc.Lines.First(l => l.Kind == DiffLineKind.Added);
        Assert.Equal("    nSpeed : INT := 200;", added.Text);
        Assert.Equal("+", added.Marker);
    }

    [Fact]
    public void Line_numbers_are_tracked_from_the_hunk_header()
    {
        // The difference between a diff you can discuss ("line 213") and one you can only
        // squint at.
        var doc = UnifiedDiffParser.Parse(Sample);

        var context = doc.Lines.First(l => l.Kind == DiffLineKind.Context);
        Assert.Equal(10, context.OldLine);
        Assert.Equal(10, context.NewLine);

        var removed = doc.Lines.First(l => l.Kind == DiffLineKind.Removed);
        Assert.Equal(12, removed.OldLine);
        Assert.Null(removed.NewLine);
    }

    [Fact]
    public void Added_lines_advance_only_the_new_side()
    {
        var doc = UnifiedDiffParser.Parse(Sample);
        var added = doc.Lines.Where(l => l.Kind == DiffLineKind.Added).ToList();

        Assert.All(added, l => Assert.Null(l.OldLine));
        Assert.Equal([12, 13], added.Select(l => l.NewLine!.Value));
    }

    [Fact]
    public void A_file_header_block_becomes_one_title_with_the_file_counts()
    {
        // diff --git, index, --- and +++ say only "this file"; a commit of twelve files read
        // as one run of text with the boundaries the hardest thing to find.
        var doc = UnifiedDiffParser.Parse(Sample);

        var title = Assert.Single(doc.Lines, l => l.Kind == DiffLineKind.FileTitle);
        Assert.Equal("plc/Main.st", title.Text);
        Assert.Equal(new DiffFileTitle("plc/Main.st", null, DiffFileChange.Modified, 2, 1), title.Title);
        Assert.DoesNotContain(doc.Lines, l => l.Text.StartsWith("diff --git", StringComparison.Ordinal)
                                           || l.Text.StartsWith("index ", StringComparison.Ordinal)
                                           || l.Text.StartsWith("+++", StringComparison.Ordinal));
        Assert.Equal([title.Title!], doc.Files);
    }

    [Fact]
    public void Each_file_of_a_commit_gets_its_own_title_and_counts()
    {
        var doc = UnifiedDiffParser.Parse("""
            diff --git a/docs/STATUS.md b/docs/STATUS.md
            index 1..2 100644
            --- a/docs/STATUS.md
            +++ b/docs/STATUS.md
            @@ -1 +1 @@
            -old
            +new
            diff --git a/plc/New.st b/plc/New.st
            new file mode 100644
            index 0000000..3
            --- /dev/null
            +++ b/plc/New.st
            @@ -0,0 +1,2 @@
            +one
            +two
            diff --git a/plc/Gone.st b/plc/Gone.st
            deleted file mode 100644
            index 4..0000000
            --- a/plc/Gone.st
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            diff --git a/plc/Old Name.st b/plc/New Name.st
            similarity index 100%
            rename from plc/Old Name.st
            rename to plc/New Name.st
            """);

        Assert.Equal(
            [
                new DiffFileTitle("docs/STATUS.md", null, DiffFileChange.Modified, 1, 1),
                new DiffFileTitle("plc/New.st", null, DiffFileChange.Added, 2, 0),
                new DiffFileTitle("plc/Gone.st", null, DiffFileChange.Deleted, 0, 1),
                new DiffFileTitle("plc/New Name.st", "plc/Old Name.st", DiffFileChange.Renamed, 0, 0),
            ],
            doc.Files);
    }

    [Fact]
    public void A_path_with_spaces_survives_the_diff_git_line()
    {
        // A header with no +++ line (a mode change) takes its path from `diff --git` alone,
        // which is ambiguous when the path contains " b/".
        var doc = UnifiedDiffParser.Parse(
            "diff --git a/Device/Plc Logic/a b/x.st b/Device/Plc Logic/a b/x.st\n" +
            "old mode 100644\n" +
            "new mode 100755\n");

        Assert.Equal("Device/Plc Logic/a b/x.st", Assert.Single(doc.Files).Path);

        // And what the title does not say stays on screen.
        Assert.Contains(doc.Lines, l => l is { Kind: DiffLineKind.FileHeader, Text: "new mode 100755" });
    }

    [Fact]
    public void A_bare_hunk_with_no_diff_git_line_keeps_its_header_lines()
    {
        var doc = UnifiedDiffParser.Parse("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n");

        Assert.Empty(doc.Files);
        Assert.Contains(doc.Lines, l => l is { Kind: DiffLineKind.FileHeader, Text: "+++ b/x" });
    }

    [Fact]
    public void A_removed_line_that_looks_like_a_header_is_content()
    {
        // "--- x" inside a hunk is a removed line reading "-- x". Folding is only for the
        // header block before a file's first @@.
        var doc = UnifiedDiffParser.Parse(
            "diff --git a/n.md b/n.md\n--- a/n.md\n+++ b/n.md\n@@ -1 +1 @@\n--- x\n+++ y\n");

        Assert.Equal(new DiffFileTitle("n.md", null, DiffFileChange.Modified, 1, 1), Assert.Single(doc.Files));
    }

    [Fact]
    public void A_removed_line_that_looks_like_a_header_is_still_a_removed_line()
    {
        // "-- x" removed arrives as "--- x"; "++ y" added as "+++ y". Inside a hunk those are
        // content. They were taken for file headers - dimmed, uncounted, and every line
        // number after them one out.
        var doc = UnifiedDiffParser.Parse("@@ -1,2 +1,2 @@\n--- x\n+++ y\n same\n");

        Assert.Equal(1, doc.Removed);
        Assert.Equal(1, doc.Added);
        Assert.Equal("-- x", doc.Lines.Single(l => l.Kind == DiffLineKind.Removed).Text);
        Assert.Equal(2, doc.Lines.Single(l => l.Kind == DiffLineKind.Context).NewLine);
    }

    [Fact]
    public void The_function_context_after_a_hunk_header_does_not_move_its_start()
    {
        // git prints the enclosing line after the second @@, and it can begin with - or +.
        var doc = UnifiedDiffParser.Parse("@@ -10,1 +20,1 @@ -x +y\n ctx\n");

        var context = Assert.Single(doc.Lines, l => l.Kind == DiffLineKind.Context);
        Assert.Equal(10, context.OldLine);
        Assert.Equal(20, context.NewLine);
    }

    [Fact]
    public void A_binary_diff_is_detected()
    {
        var doc = UnifiedDiffParser.Parse(
            "diff --git a/x.project b/x.project\n" +
            "Binary files a/x.project and b/x.project differ");

        Assert.True(doc.IsBinary);
        Assert.Contains("Binary", doc.Summary);
    }

    [Fact]
    public void Several_hunks_each_reset_the_line_numbers()
    {
        var doc = UnifiedDiffParser.Parse("""
            @@ -1,3 +1,3 @@
             a
            -b
            +B
            @@ -100,3 +100,3 @@
             x
            -y
            +Y
            """);

        var removed = doc.Lines.Where(l => l.Kind == DiffLineKind.Removed).ToList();
        Assert.Equal(2, removed[0].OldLine);
        Assert.Equal(101, removed[1].OldLine);
    }

    [Fact]
    public void A_no_newline_marker_is_its_own_kind()
    {
        var doc = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+b");

        Assert.Contains(doc.Lines, l => l.Kind == DiffLineKind.NoNewline);
    }

    [Fact]
    public void A_line_that_is_only_a_plus_is_an_added_empty_line()
    {
        var doc = UnifiedDiffParser.Parse("@@ -1,1 +1,2 @@\n a\n+");

        var added = doc.Lines.Last(l => l.Kind == DiffLineKind.Added);
        Assert.Equal(string.Empty, added.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_an_empty_document(string? input)
    {
        Assert.True(UnifiedDiffParser.Parse(input).IsEmpty);
    }

    [Fact]
    public void Crlf_input_parses_identically()
    {
        var lf = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n+b");
        var crlf = UnifiedDiffParser.Parse("@@ -1 +1 @@\r\n-a\r\n+b");

        Assert.Equal(lf.Added, crlf.Added);
        Assert.Equal(lf.Lines.Count, crlf.Lines.Count);
    }
}

public class LanguageCatalogTests
{
    [Theory]
    [InlineData("plc/Main.st", "Structured Text")]
    [InlineData("plc/FB_Motor.scl", "Structured Text")]
    [InlineData("plc/Type.udt", "Structured Text")]
    [InlineData("plc/FB_Motor.s7dcl", "Structured Text")]
    [InlineData("src/Program.cs", "C#")]
    [InlineData("tools/build.py", "Python")]
    [InlineData("Visualization.vis.xml", "XML")]
    [InlineData("app.csproj", "XML")]
    [InlineData("_manifest.json", "JSON")]
    [InlineData(".github/workflows/ci.yml", "YAML")]
    [InlineData("README.md", "Markdown")]
    [InlineData("tools/Run.ps1", "PowerShell")]
    [InlineData("web/app.ts", "JavaScript")]
    [InlineData("db/schema.sql", "SQL")]
    public void The_extension_selects_the_language(string path, string expected)
    {
        Assert.Equal(expected, LanguageCatalog.ForPath(path).Name);
    }

    [Fact]
    public void Extension_matching_is_case_insensitive()
    {
        Assert.Equal("C#", LanguageCatalog.ForPath("Program.CS").Name);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("data.bin")]
    [InlineData("noextension")]
    [InlineData(null)]
    public void An_unknown_file_falls_back_to_plain_text(string? path)
    {
        // Guessing wrong is worse than not guessing: mis-coloured code reads as broken.
        Assert.Same(SyntaxLanguage.PlainText, LanguageCatalog.ForPath(path));
    }

    [Fact]
    public void A_shebang_identifies_an_extensionless_script()
    {
        Assert.Equal("Python",
            LanguageCatalog.ForContent("scripts/deploy", "#!/usr/bin/env python3").Name);
    }

    [Fact]
    public void An_extension_wins_over_a_shebang()
    {
        Assert.Equal("C#",
            LanguageCatalog.ForContent("Program.cs", "#!/usr/bin/env python3").Name);
    }
}

public class SyntaxTokenizerTests
{
    private static SyntaxLanguage St => LanguageCatalog.ForPath("x.st");
    private static SyntaxLanguage CSharp => LanguageCatalog.ForPath("x.cs");
    private static SyntaxLanguage Python => LanguageCatalog.ForPath("x.py");

    private static IEnumerable<(string Text, TokenKind Kind)> Tokens(string line, SyntaxLanguage lang) =>
        SyntaxTokenizer.Tokenize(line, lang)
            .Select(s => (line.Substring(s.Start, s.Length), s.Kind));

    /// <summary>
    /// The invariant that matters more than any colour: spans cover the line exactly once.
    /// A tokenizer bug must never be able to hide or duplicate a changed line.
    /// </summary>
    private static void AssertCoversExactly(string line, SyntaxLanguage language)
    {
        var spans = SyntaxTokenizer.Tokenize(line, language);
        var position = 0;
        foreach (var span in spans)
        {
            Assert.Equal(position, span.Start);
            Assert.True(span.Length > 0, "a zero-length span would loop forever");
            position += span.Length;
        }

        Assert.Equal(line.Length, position);
    }

    [Fact]
    public void ST_keywords_and_types_are_distinguished()
    {
        var tokens = Tokens("VAR nSpeed : INT := 100; END_VAR", St).ToList();

        Assert.Contains(tokens, t => t is { Text: "VAR", Kind: TokenKind.Keyword });
        Assert.Contains(tokens, t => t is { Text: "INT", Kind: TokenKind.Type });
        Assert.Contains(tokens, t => t is { Text: "100", Kind: TokenKind.Number });
    }

    [Fact]
    public void ST_keywords_are_case_insensitive()
    {
        // IEC 61131-3 is case-insensitive, and real projects mix cases freely.
        Assert.Contains(Tokens("if bStart then", St), t => t is { Text: "if", Kind: TokenKind.Keyword });
    }

    [Fact]
    public void An_ST_line_comment_runs_to_end_of_line()
    {
        var tokens = Tokens("nSpeed := 1; // set the speed", St).ToList();

        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment && t.Text.Contains("set the speed"));
    }

    [Fact]
    public void An_ST_block_comment_is_one_span()
    {
        var tokens = Tokens("(* a comment *) VAR", St).ToList();

        Assert.Equal(TokenKind.Comment, tokens[0].Kind);
        Assert.Equal("(* a comment *)", tokens[0].Text);
    }

    [Fact]
    public void An_unterminated_block_comment_colours_to_end_of_line_only()
    {
        // A diff shows fragments, so an opener with no closer in this hunk is normal. It
        // must not swallow anything beyond the line it is on.
        var line = "(* opened but never closed";
        var spans = SyntaxTokenizer.Tokenize(line, St);

        Assert.Equal(TokenKind.Comment, Assert.Single(spans).Kind);
        AssertCoversExactly(line, St);
    }

    [Fact]
    public void An_attribute_pragma_claims_the_whole_line()
    {
        var spans = SyntaxTokenizer.Tokenize("{attribute 'qualified_only'}", St);

        Assert.Equal(TokenKind.Directive, Assert.Single(spans).Kind);
    }

    [Fact]
    public void ST_single_quoted_strings_are_recognised()
    {
        Assert.Contains(Tokens("s := 'Kühlschrank';", St),
            t => t is { Kind: TokenKind.String, Text: "'Kühlschrank'" });
    }

    [Fact]
    public void CSharp_keywords_are_case_sensitive()
    {
        // "Class" is an identifier in C#, not a keyword.
        Assert.DoesNotContain(Tokens("Class x", CSharp), t => t.Kind == TokenKind.Keyword);
        Assert.Contains(Tokens("class x", CSharp), t => t is { Text: "class", Kind: TokenKind.Keyword });
    }

    [Fact]
    public void A_CSharp_preprocessor_line_is_a_directive()
    {
        Assert.Equal(TokenKind.Directive,
            Assert.Single(SyntaxTokenizer.Tokenize("#nullable enable", CSharp)).Kind);
    }

    [Fact]
    public void Python_comments_use_hash()
    {
        Assert.Contains(Tokens("x = 1  # a note", Python),
            t => t.Kind == TokenKind.Comment && t.Text.Contains("a note"));
    }

    [Fact]
    public void An_escaped_quote_does_not_end_a_string()
    {
        var tokens = Tokens("""var s = "a \" b"; // after""", CSharp).ToList();

        Assert.Contains(tokens, t => t is { Kind: TokenKind.String } && t.Text.Contains("\\\""));
        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void An_identifier_containing_digits_is_not_split_into_a_number()
    {
        var tokens = Tokens("nSpeed2 := 1;", St).ToList();

        Assert.Contains(tokens, t => t is { Text: "nSpeed2", Kind: TokenKind.Plain });
    }

    [Fact]
    public void Hex_and_IEC_based_literals_are_single_numbers()
    {
        Assert.Contains(Tokens("x := 16#FF;", St), t => t.Text.Contains("16#FF"));
        Assert.Contains(Tokens("var x = 0xFF;", CSharp), t => t is { Text: "0xFF", Kind: TokenKind.Number });
    }

    [Fact]
    public void Plain_text_is_a_single_span_and_nothing_is_highlighted()
    {
        var spans = SyntaxTokenizer.Tokenize("anything at all 123 // not a comment",
            SyntaxLanguage.PlainText);

        Assert.Equal(TokenKind.Plain, Assert.Single(spans).Kind);
    }

    [Theory]
    [InlineData("x.st", "VAR nSpeed : INT := 16#FF; (* c *) // trailing")]
    [InlineData("x.cs", "public async Task<int> M() => await X(1.5e-3, \"s\"); // c")]
    [InlineData("x.py", "def f(a: int = 3) -> str:  # note")]
    [InlineData("x.xml", "<Single Name=\"Timestamp\" Type=\"long\">0</Single>")]
    [InlineData("x.json", "{ \"a\": [1, 2.5, true, null] }")]
    [InlineData("x.yml", "key: value  # comment")]
    [InlineData("x.md", "# Heading with `code`")]
    [InlineData("x.ps1", "$x = Get-Item -Path 'C:\\y'  # note")]
    [InlineData("x.ts", "export const f = async (): Promise<void> => { /* c */ };")]
    [InlineData("x.sql", "SELECT * FROM t WHERE a = 'b' -- note")]
    public void Spans_always_cover_the_line_exactly_once(string path, string line)
    {
        // Highlighting is decoration; the diff is the information. This is what guarantees
        // a tokenizer bug can never hide or duplicate a changed line.
        AssertCoversExactly(line, LanguageCatalog.ForPath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\t")]
    [InlineData("((((")]
    [InlineData("\"unterminated")]
    [InlineData("'")]
    [InlineData("////")]
    [InlineData("12345")]
    [InlineData("→ ünïcödé ✓")]
    public void Adversarial_lines_do_not_break_the_coverage_invariant(string line)
    {
        foreach (var path in new[] { "x.st", "x.cs", "x.py", "x.json", "x.md" })
        {
            AssertCoversExactly(line, LanguageCatalog.ForPath(path));
        }
    }

    [Fact]
    public void Every_supported_language_tokenizes_a_realistic_line_safely()
    {
        // Guards against a new language being added with a table that loops or drops text.
        const string line = "abc 123 \"str\" 'chr' // trail /* block */ (* st *) # hash";

        foreach (var extension in LanguageCatalog.SupportedExtensions)
        {
            AssertCoversExactly(line, LanguageCatalog.ForPath("file" + extension));
        }
    }
}
