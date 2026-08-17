namespace Versio.Core.Text;

/// <summary>
/// The characters Versio trims from the edge of a text payload, named rather than inherited
/// from a language default.
/// </summary>
/// <remarks>
/// <para>
/// <b>Because <c>string.Trim()</c> and python's <c>str.strip()</c> do not agree, and three
/// separate pieces of this product compare a C# answer against a python one.</b> Trim removes
/// 25 codepoints and <c>str.strip()</c> removes 29; the four extra are the ASCII separators
/// U+001C–U+001F, which python calls whitespace and .NET does not. Enumerated on both
/// sides rather than read off either documentation — nothing else in the range scanned
/// differed.
/// </para>
/// <para>
/// <b>What the divergence cost.</b> Two of the three surfaces feed a hash that pairs a renamed
/// object with the file it became:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Iec.StTextCodec"/> ↔ <c>versio_text.split_st</c> — the exporter records
/// <c>ihash</c> over python's implementation half and the planner re-computes it over the C#
/// one. Different trims, different hashes, no rename detected, and the object is destroyed and
/// rebuilt with a new GUID.
/// </description></item>
/// <item><description>
/// <see cref="Diff.LineSketch"/> ↔ <c>versio_text.line_sketch</c> — the same story for
/// the fallback that pairs a renamed-and-edited file on a mirror git never saw. A line whose
/// edge differs hashes differently, the sketches diverge, and the similarity is under-reported.
/// </description></item>
/// <item><description>
/// <see cref="Diff.TextSimilarity"/> has no python mirror, and is here because
/// <c>line_sketch</c>'s own docstring promises the two "agree about what a line is". A shared
/// definition is what makes that true rather than aspirational.
/// </description></item>
/// </list>
/// <para>
/// <b>There is a fourth pair that trims and is deliberately NOT here, checked rather than
/// assumed.</b> <see cref="Iec.StDeclarationParser"/> and <c>versio_decl.parse_declaration</c>
/// both parse a declaration and both trim, and they use their own language's default. They can
/// diverge without breaking anything, because <b>nothing compares one against the other</b>:
/// the planner takes the C# side's <i>name</i> and puts it in the plan, the script takes its own
/// side's <i>kind</i> and picks a <c>create_*</c> call with it, and the two answers are
/// different questions. The worst a disagreement can do is have python decline a declaration C#
/// accepted, which is <b>reported</b> as <c>CANNOT_IDENTIFY_DECLARATION</c> rather than applied
/// silently. That is the whole distinction: the three above feed a hash or an equality test, and
/// this one feeds a person.
/// </para>
/// <para>
/// <b>The set is python's</b>, because the exporter is the writer and every hash in every
/// committed manifest came from it. No text file in either committed mirror contains any of the
/// four, so adopting it changed no committed byte — verified by re-exporting both fixtures
/// and diffing rather than by argument.
/// </para>
/// <para>
/// <b>One home for one rule.</b> <c>ExportCoverage.IsExportOnly</c> records what two homes cost
/// the last time: each copy was right for the caller in front of it and wrong for the next.
/// <c>PythonFileNameParityTests</c> compares this array against <c>versio_text.WS</c> directly,
/// so a character added on one side and not the other fails without needing a payload that
/// happens to contain it.
/// </para>
/// </remarks>
public static class PayloadWhitespace
{
    /// <summary>
    /// The 29 codepoints, ascending.
    /// </summary>
    /// <remarks>
    /// An array because <c>string.Trim</c> takes one, and static because copying it per access
    /// would be an allocation on every line <see cref="Diff.LineSketch"/> reads. Callers must
    /// not write to it — nothing here does, and the parity test would report the change as
    /// a divergence if anything did.
    /// </remarks>
    public static readonly char[] Characters =
    [
        '\t', '\n', '\v', '\f', '\r', '\u001c', '\u001d', '\u001e', '\u001f', ' ',
        '\u0085', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006',
        '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000',
    ];

    /// <summary>Trim both ends of <paramref name="text"/> with that set.</summary>
    public static string Trim(string text) => text.Trim(Characters);
}
