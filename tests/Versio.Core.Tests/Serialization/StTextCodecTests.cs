using Versio.Core.Serialization;
using Xunit;

namespace Versio.Core.Tests.Serialization;

/// <summary>
/// The highest-consequence pure code in the product: a bug here silently corrupts PLC
/// source on import. Tested harder than anything else.
/// </summary>
public class StTextCodecTests
{
    [Fact]
    public void Join_places_delimiter_between_declaration_and_implementation()
    {
        var result = StTextCodec.Join("PROGRAM Main\nVAR\nEND_VAR", "x := 1;");

        Assert.Contains(StTextCodec.ImplementationDelimiter, result);
        Assert.StartsWith("PROGRAM Main", result);
        Assert.EndsWith("x := 1;\n", result);
    }

    [Fact]
    public void Split_is_the_inverse_of_Join()
    {
        const string decl = "FUNCTION_BLOCK Conveyor\nVAR_INPUT\n  nSpeed : INT;\nEND_VAR";
        const string impl = "IF bStart THEN\n  nSpeed := 100;\nEND_IF";

        var (d, i) = StTextCodec.Split(StTextCodec.Join(decl, impl));

        Assert.Equal(decl, d);
        Assert.Equal(impl, i);
    }

    [Theory]
    // Ordinary code
    [InlineData("PROGRAM A\nVAR\nEND_VAR", "a := 1;")]
    // Empty implementation - a declaration-only POU is legal
    [InlineData("PROGRAM A\nVAR\nEND_VAR", "")]
    // Empty declaration
    [InlineData("", "a := 1;")]
    // Both empty
    [InlineData("", "")]
    // Non-ASCII: this is exactly what killed python 2's builtin open() for codescribe
    [InlineData("VAR\n  s : STRING := 'Kühlschrank';\nEND_VAR", "// Temperatur 0.1°C senken")]
    [InlineData("VAR (* Кириллица *)\nEND_VAR", "// смарт — punctuation")]
    // Tabs and mixed indentation must survive verbatim
    [InlineData("VAR\n\tx : INT;\n\t\ty : INT;\nEND_VAR", "\tIF x THEN\n\t\ty := 1;\n\tEND_IF")]
    // Blank lines inside the body
    [InlineData("VAR\nEND_VAR", "a := 1;\n\n\nb := 2;")]
    public void Round_trip_preserves_content(string decl, string impl)
    {
        var (d, i) = StTextCodec.Split(StTextCodec.Join(decl, impl));

        Assert.Equal(decl.Replace("\r\n", "\n").Trim(), d);
        Assert.Equal(impl.Replace("\r\n", "\n").Trim(), i);
    }

    [Fact]
    public void Delimiter_appearing_inside_the_DECLARATION_does_not_corrupt_the_split()
    {
        // Regression: found by the randomised round-trip test below. Before the escaping
        // fix, a declaration containing the delimiter made Split cut in the wrong place and
        // silently move code from the declaration into the implementation.
        var decl = $"PROGRAM A\n{StTextCodec.ImplementationDelimiter}\nVAR\nEND_VAR";
        const string impl = "a := 1;";

        var (d, i) = StTextCodec.Split(StTextCodec.Join(decl, impl));

        Assert.Equal(decl, d);
        Assert.Equal(impl, i);
    }

    [Fact]
    public void Delimiter_in_BOTH_halves_still_round_trips()
    {
        var decl = $"PROGRAM A {StTextCodec.ImplementationDelimiter} x";
        var impl = $"a := 1; {StTextCodec.ImplementationDelimiter} b := 2;";

        var (d, i) = StTextCodec.Split(StTextCodec.Join(decl, impl));

        Assert.Equal(decl, d);
        Assert.Equal(impl, i);
    }

    [Fact]
    public void Content_containing_the_escape_marker_itself_round_trips()
    {
        // The escape scheme has to escape its own marker, or content that happens to
        // contain it would be mangled on the way back.
        const string decl = "PROGRAM A //!VERSIO-ESC!D and //!VERSIO-ESC!E";
        const string impl = "a := 1; //!VERSIO-ESC!";

        var (d, i) = StTextCodec.Split(StTextCodec.Join(decl, impl));

        Assert.Equal(decl, d);
        Assert.Equal(impl, i);
    }

    [Fact]
    public void Ordinary_content_is_left_completely_untouched_by_escaping()
    {
        // The escaping must be invisible in the normal case: no marker noise in the file.
        var joined = StTextCodec.Join("PROGRAM A\nVAR\nEND_VAR", "a := 1;");

        Assert.DoesNotContain("VERSIO-ESC", joined);
    }

    [Fact]
    public void Delimiter_appearing_inside_implementation_does_not_corrupt_the_split()
    {
        // The delimiter is a legal ST comment, so a user can legitimately type it.
        // Splitting on the last occurrence - or on all of them - would silently eat code.
        const string decl = "PROGRAM A\nVAR\nEND_VAR";
        var impl = $"a := 1;\n{StTextCodec.ImplementationDelimiter}\nb := 2;";

        var (d, i) = StTextCodec.Split(StTextCodec.Join(decl, impl));

        Assert.Equal(decl, d);
        Assert.Equal(impl, i);
    }

    [Fact]
    public void Split_without_delimiter_treats_everything_as_declaration()
    {
        // DUTs and GVLs are declaration-only and have no delimiter.
        var (d, i) = StTextCodec.Split("TYPE E_State :\n(\n  Idle := 0\n);\nEND_TYPE");

        Assert.StartsWith("TYPE E_State", d);
        Assert.Equal(string.Empty, i);
    }

    [Fact]
    public void Crlf_is_normalized_to_lf()
    {
        // Git and CODESYS disagree about line endings; if we did not normalise, every file
        // would appear changed on a machine with different core.autocrlf settings.
        var joined = StTextCodec.Join("A\r\nB", "C\r\nD");

        Assert.DoesNotContain("\r", joined);
    }

    [Fact]
    public void Join_is_idempotent_through_a_round_trip()
    {
        // This is what makes export -> import -> export byte-identical instead of
        // accumulating whitespace on every cycle.
        var once = StTextCodec.Join("  PROGRAM A  \n", "\n\n  a := 1;  \n\n");
        var (d, i) = StTextCodec.Split(once);
        var twice = StTextCodec.Join(d, i);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Repeated_round_trips_reach_a_fixed_point()
    {
        var text = StTextCodec.Join("PROGRAM A\nVAR\nEND_VAR", "a := 1;");
        for (var n = 0; n < 10; n++)
        {
            var (d, i) = StTextCodec.Split(text);
            var next = StTextCodec.Join(d, i);
            Assert.Equal(text, next);
            text = next;
        }
    }

    [Fact]
    public void DeclarationOnly_ends_with_exactly_one_newline()
    {
        Assert.Equal("TYPE X : INT; END_TYPE\n", StTextCodec.DeclarationOnly("  TYPE X : INT; END_TYPE  \n\n\n"));
    }

    [Fact]
    public void Null_input_is_tolerated()
    {
        var joined = StTextCodec.Join(null, null);
        var (d, i) = StTextCodec.Split(null);

        Assert.Contains(StTextCodec.ImplementationDelimiter, joined);
        Assert.Equal(string.Empty, d);
        Assert.Equal(string.Empty, i);
    }

    [Fact]
    public void ContentHash_is_stable_and_sensitive()
    {
        var a = StTextCodec.ContentHash("hello");
        var b = StTextCodec.ContentHash("hello");
        var c = StTextCodec.ContentHash("hello ");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("sha256:", a);
    }

    [Fact]
    public void ContentHash_has_no_bom_influence()
    {
        // A BOM would change the hash of otherwise identical content, producing phantom
        // diffs. We encode without one, deliberately.
        var text = "PROGRAM A";
        var expected = StTextCodec.ContentHash(System.Text.Encoding.UTF8.GetBytes(text));

        Assert.Equal(expected, StTextCodec.ContentHash(text));
    }

    [Fact]
    public void Round_trip_survives_pseudo_random_adversarial_text()
    {
        // Stands in for a property-based test until FsCheck is vendored.
        var rng = new Random(20260811);
        var alphabet = new[]
        {
            "PROGRAM", "VAR", "END_VAR", "\n", "\r\n", "\t", "  ", "°C", "Ω", "ä", "→",
            "// comment", "(* block *)", ";", ":=", "'quoted'", StTextCodec.ImplementationDelimiter,
        };

        for (var n = 0; n < 500; n++)
        {
            var decl = string.Concat(Enumerable.Range(0, rng.Next(0, 12))
                .Select(_ => alphabet[rng.Next(alphabet.Length)]));
            var impl = string.Concat(Enumerable.Range(0, rng.Next(0, 12))
                .Select(_ => alphabet[rng.Next(alphabet.Length)]));

            var joined = StTextCodec.Join(decl, impl);
            var (d2, i2) = StTextCodec.Split(joined);
            var rejoined = StTextCodec.Join(d2, i2);

            // Two invariants. Re-joining is a fixed point, so an unchanged object never
            // produces a diff no matter how odd its content...
            Assert.Equal(joined, rejoined);

            // ...and the halves come back as they went in. This is the one that caught the
            // delimiter-in-declaration bug.
            Assert.Equal(decl.Replace("\r\n", "\n").Trim(), d2);
            Assert.Equal(impl.Replace("\r\n", "\n").Trim(), i2);
        }
    }
}
