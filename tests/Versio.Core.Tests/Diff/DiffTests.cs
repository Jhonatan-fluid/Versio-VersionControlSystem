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
    public void File_headers_are_kept_but_marked_as_such()
    {
        var doc = UnifiedDiffParser.Parse(Sample);

        Assert.Contains(doc.Lines, l => l.Kind == DiffLineKind.FileHeader &&
                                        l.Text.StartsWith("diff --git", StringComparison.Ordinal));
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
