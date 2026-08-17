using System.Text.RegularExpressions;
using Versio.Core.Plc;

namespace Versio.Core.Serialization;

/// <summary>
/// Removes volatile fields from CODESYS native export XML so that exporting an unchanged
/// project twice produces byte-identical output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only <c>Timestamp</c> is scrubbed. GUIDs and Ids are deliberately left alone.</b>
/// codescribe tried canonicalising them and it broke visualisations — their source carries
/// the warning "Overwriting Id's broke visualisations… Stick to just overwriting timestamps".
/// GUIDs are identity, not noise: they are what lets a moved object be recognised as a move
/// rather than a delete plus an add.
/// </para>
/// <para>
/// Verified on CODESYS 3.5.22.20 against RefrigeratorControl.project: two consecutive
/// exports were byte-identical across all 10 files (2.9 MB), with 9 Timestamp fields zeroed.
/// </para>
/// </remarks>
public static partial class NativeXmlScrubber
{
    [GeneratedRegex("""(?<pre><Single Name="Timestamp" Type="long">)(?<val>[^<]*)(?<post></Single>)""")]
    private static partial Regex TimestampPattern();

    /// <summary>Scrubbed text, and how many substitutions it took.</summary>
    public readonly record struct ScrubResult(string Text, int Replacements);

    /// <summary>Canonicalise a native export payload.</summary>
    public static ScrubResult Scrub(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new ScrubResult(string.Empty, 0);
        }

        var normalized = raw.Replace("\r\n", "\n");
        var count = 0;
        var scrubbed = TimestampPattern().Replace(normalized, m =>
        {
            count++;
            return m.Groups["pre"].Value + "0" + m.Groups["post"].Value;
        });

        if (!scrubbed.EndsWith('\n'))
        {
            scrubbed += "\n";
        }

        return new ScrubResult(scrubbed, count);
    }

    /// <summary>
    /// Scrubbing must be idempotent: applying it to already-scrubbed content is a no-op.
    /// Asserted by a property test, because the hash gate re-scrubs content it has
    /// previously written.
    /// </summary>
    public static bool IsScrubbed(string text) =>
        !TimestampPattern().Matches(text).Any(m => m.Groups["val"].Value != "0");
}
