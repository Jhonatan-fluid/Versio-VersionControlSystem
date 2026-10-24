using System.Text.RegularExpressions;
using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// docs/web/PLAN.md security rule 5, the second wall: whatever a file holds, the code renderers
/// emit only the tags and attributes they are known to emit.
/// </summary>
/// <remarks>
/// The first wall is the escaping, and each renderer's own tests check it on chosen inputs. This
/// is the same claim over seeded random input built from the characters that matter to HTML and
/// to the hidden-character rule, through every renderer and view, so a new code path that
/// forgot to escape shows up as a tag or attribute nobody listed. The page's DOMPurify is the
/// third wall and the CSP behind all three.
/// </remarks>
public sealed partial class RendererAllowlistTests
{
    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "div", "span", "code", "del", "ins", "mark", "p", "table", "thead", "tbody", "tr", "th", "td", "a",
    };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "class", "title", "colspan",
    };

    private static readonly char[] Alphabet =
    [
        '<', '>', '&', '"', '\'', '/', '=', ' ', 'a', 's', 'c', 'r', 'i', 'p', 't', 'o', 'n', '(', '*', ')', ':', ';', '\t', '1',
        (char)0x202E, (char)0x200B, (char)0x0000, (char)0x2028,
    ];

    private static string Noise(Random random, int max) =>
        new(Enumerable.Range(0, random.Next(0, max)).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray());

    [Fact]
    public void Every_renderer_over_random_input_emits_only_known_tags_and_attributes()
    {
        var random = new Random(20261011);
        for (var n = 0; n < 200; n++)
        {
            var older = string.Join('\n', Enumerable.Range(0, random.Next(1, 8)).Select(_ => Noise(random, 30)));
            var newer = string.Join('\n', Enumerable.Range(0, random.Next(1, 8)).Select(_ => Noise(random, 30)));
            var rows = StSourceLayout.Mark(StSourceLayout.Lay(newer), older, newer);
            var diff = UnifiedDiffParser.Parse(UnifiedDiffWriter.Write(older, newer));
            var table = MemberTableDiff.Compare(
                new MemberTable { Name = Noise(random, 10) + "T", Members = [new MemberRow { Name = Noise(random, 10), Path = Noise(random, 10), Datatype = Noise(random, 10), StartValue = "1" }] },
                new MemberTable
                {
                    Name = Noise(random, 10) + "T",
                    InstanceOf = Noise(random, 10),
                    Members =
                    [
                        new MemberRow { Name = Noise(random, 10), Path = Noise(random, 10), Datatype = Noise(random, 10), StartValue = "2", Comment = Noise(random, 20), LinkGuid = Noise(random, 8), ExternalAccess = Noise(random, 5), Depth = 1 },
                        new MemberRow { Name = Noise(random, 10), Path = Noise(random, 10), IsSectionHeading = true },
                    ],
                });

            foreach (var html in new[]
                     {
                         SourceHtml.Fragment(rows, LanguageCatalog.StructuredText),
                         SourceHtml.Fragment(rows, LanguageCatalog.Xml, SourceView.SideBySide),
                         TextDiffHtml.Fragment(diff, LanguageCatalog.StructuredText),
                         TextDiffHtml.Fragment(diff, SyntaxLanguage.PlainText, TextDiffView.SideBySide),
                         MemberTableHtml.Fragment(table),
                         CatalogHtml.Fragment(CatalogDiff.Compare(
                             new LibraryCatalog { Schema = 1, Manager = Noise(random, 10), References = [new LibraryReference { Name = Noise(random, 8), DefaultResolution = "1" }] },
                             new LibraryCatalog { Schema = 1, Manager = Noise(random, 10), References = [new LibraryReference { Name = Noise(random, 8), DefaultResolution = Noise(random, 6), Namespace = Noise(random, 6) }] }), compared: true, sourcePath: Noise(random, 10)),
                         CatalogHtml.Fragment(new TaskCatalog { Schema = 1, Configuration = Noise(random, 10), Tasks = [new TaskRecord { Name = Noise(random, 8), Priority = Noise(random, 4), Pous = [Noise(random, 8)] }] }),
                         CatalogHtml.Fragment(new DeviceCatalog
                         {
                             Schema = 1,
                             Name = Noise(random, 10),
                             Connectors = [new DeviceConnector { ConnectorId = Noise(random, 4), Interface = Noise(random, 8), Parameters = new Dictionary<string, string>(StringComparer.Ordinal) }],
                             Slots = new Dictionary<string, string>(StringComparer.Ordinal) { [Noise(random, 4) + "s"] = Noise(random, 8) },
                         }),
                         CardHtml.Fragment("task", Noise(random, 10), [new CardFact(Noise(random, 8), Noise(random, 20))], Noise(random, 10)),
                     })
            {
                foreach (Match tag in Tag().Matches(html))
                {
                    Assert.True(Tags.Contains(tag.Groups["name"].Value), $"unknown tag <{tag.Groups["name"].Value}> from input {Visible(older)} / {Visible(newer)}");
                    foreach (Match attribute in Attribute().Matches(tag.Groups["attrs"].Value))
                    {
                        var name = attribute.Groups["name"].Value;
                        Assert.True(Attributes.Contains(name) || name.StartsWith("data-", StringComparison.Ordinal),
                            $"unknown attribute {name} on <{tag.Groups["name"].Value}> from input {Visible(older)} / {Visible(newer)}");
                    }
                }

                // Any '<' that is not one of the renderer's own tags is a file's text that escaped.
                Assert.Equal(html.Count(c => c == '<'), Tag().Matches(html).Count + CloseTag().Matches(html).Count);
            }
        }
    }

    private static string Visible(string s) =>
        string.Concat(s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"<U+{(int)c:X4}>"));

    [GeneratedRegex("<(?<name>[a-z]+)(?<attrs>(?:\\s+[a-z-]+=\"[^\"]*\")*)\\s*/?>")]
    private static partial Regex Tag();

    [GeneratedRegex("</[a-z]+>")]
    private static partial Regex CloseTag();

    [GeneratedRegex("(?<name>[a-z-]+)=\"[^\"]*\"")]
    private static partial Regex Attribute();
}
