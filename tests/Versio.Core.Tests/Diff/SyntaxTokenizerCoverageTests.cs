using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// The tokenizer's spans cover every character of a line, in order, with no gap and no overlap.
/// </summary>
/// <remarks>
/// Both code renderers draw only the spans this returns: <c>DiffView</c> in the window and
/// <see cref="CodeHtml"/> in a browser. A character between two spans would be missing from the
/// page while still being in the file, so a reviewer would approve code they never saw. Random
/// lines, from a fixed seed so a failure reproduces, built from the characters each language
/// treats specially: comment and string openers left unterminated, digits against letters,
/// pragma braces, tabs, and the hidden characters <see cref="CodeHtml"/> marks.
/// </remarks>
public sealed class SyntaxTokenizerCoverageTests
{
    private static readonly char[] Alphabet =
    [
        'a', 'Z', '_', '1', '9', ' ', ' ', '\t', '(', '*', ')', '/', '\'', '"', '{', '}', '#', '<', '!',
        '-', '>', ';', ':', '=', '.', '$', '\\',
        (char)0x202E, (char)0x200B, (char)0x2066, (char)0x00E9, (char)0x0430,
    ];

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var name in LanguageCatalog.SupportedExtensions
                     .Select(e => LanguageCatalog.ForPath("x" + e).Name)
                     .Append(SyntaxLanguage.PlainText.Name)
                     .Distinct())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_character_is_in_exactly_one_span(string languageName)
    {
        var language = LanguageCatalog.SupportedExtensions
            .Select(e => LanguageCatalog.ForPath("x" + e))
            .Append(SyntaxLanguage.PlainText)
            .First(l => l.Name == languageName);
        var random = new Random(20261011);

        for (var n = 0; n < 2000; n++)
        {
            var line = new string(Enumerable.Range(0, random.Next(1, 40)).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray());

            var at = 0;
            foreach (var span in SyntaxTokenizer.Tokenize(line, language))
            {
                Assert.True(span.Start == at && span.Length > 0,
                    $"{language.Name}: span {span.Start}+{span.Length} after {at} in \"{Visible(line)}\"");
                at += span.Length;
            }

            Assert.True(at == line.Length, $"{language.Name}: spans end at {at} of {line.Length} in \"{Visible(line)}\"");
        }
    }

    private static string Visible(string line) =>
        string.Concat(line.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"<U+{(int)c:X4}>"));
}
