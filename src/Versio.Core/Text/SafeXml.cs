using System.Xml;
using System.Xml.Linq;

namespace Versio.Core.Text;

/// <summary>
/// The one way a vendor pack parses XML text: no DTD, no resolver, a size cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Until 2026-10-11 the packs called <see cref="XDocument.Parse(string)"/>
/// on files the owner's own IDE wrote. The Versio Web viewer parses whatever is in a repository
/// it was pointed at, and that text is written by anyone who can push to it (docs/web/PLAN.md,
/// security rule 3). <c>XDocument.Parse</c> resolves no external entities on .NET, but it does
/// expand entities a document defines for itself, so a few hundred bytes of nested definitions
/// (a "billion laughs") cost every view of that file millions of characters of expansion.
/// </para>
/// <para>
/// <b>A DTD is refused outright.</b> Nothing a supported IDE exports carries one, so refusing
/// costs nothing real, and it removes entity expansion altogether instead of rationing it.
/// The refusal is an <see cref="XmlException"/>, which every caller already catches and turns
/// into "not drawable, show it as text", so a hostile file degrades exactly as a corrupt one
/// always has.
/// </para>
/// <para>
/// <b>The cap is generous on purpose</b>: <see cref="MaxCharacters"/> is far above the largest
/// export measured (the fixtures' visualisation files are a few MB). It bounds the worst case;
/// it is not meant to bite on a real project.
/// </para>
/// </remarks>
public static class SafeXml
{
    /// <summary>The most characters a document may hold before it is refused.</summary>
    public const long MaxCharacters = 64L * 1024 * 1024;

    /// <summary>
    /// The deepest element nesting a document may have before it is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Far beyond any real export — CODESYS's archivable format, the deepest nester among the
    /// vendors, stays two orders of magnitude under it — and a hard requirement all the same:
    /// <b><see cref="XDocument"/>'s tree building is quadratic in nesting depth.</b> Measured
    /// on 2026-10-14: a 5 MB document of 30,000 nested elements streams through a bare
    /// <see cref="XmlReader"/> in 51 ms and takes <see cref="XDocument.Load(XmlReader)"/>
    /// 70+ seconds of CPU — every setting combination alike, so it is the framework's tree,
    /// not these options. The character cap cannot catch it, because the cost is in the
    /// shape, not the size.
    /// </para>
    /// <para>
    /// Refused by a streaming pre-pass, which is what makes the refusal cheap: linear, before
    /// any tree exists. The pre-pass is paid on every parse, and on an honest document it is
    /// milliseconds against a load that costs more.
    /// </para>
    /// </remarks>
    public const int MaxDepth = 512;

    /// <summary>Parses <paramref name="xml"/>, or throws <see cref="XmlException"/>.</summary>
    /// <param name="maxCharacters">The cap; <see cref="MaxCharacters"/> unless a test needs a small one.</param>
    /// <param name="maxDepth">The nesting cap; <see cref="MaxDepth"/> unless a test needs a small one.</param>
    public static XDocument Parse(
        string xml, LoadOptions options = LoadOptions.None, long maxCharacters = MaxCharacters,
        int maxDepth = MaxDepth)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var settings = new XmlReaderSettings
        {
            // Mirrors what XDocument.Parse sets for itself (whitespace below), so the trees are
            // the same; only the DTD and the caps differ.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            IgnoreWhitespace = (options & LoadOptions.PreserveWhitespace) == 0,
        };

        // The depth pre-pass — see MaxDepth. It also means a malformed document throws here,
        // before any tree is built, which is the same XmlException the caller already handles.
        using (var probeText = new StringReader(xml))
        using (var probe = XmlReader.Create(probeText, settings))
        {
            while (probe.Read())
            {
                if (probe.Depth > maxDepth)
                {
                    throw new XmlException(
                        $"the document nests more than {maxDepth} elements deep, which no " +
                        "supported export does");
                }
            }
        }

        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, settings);
        return XDocument.Load(reader, options);
    }

    /// <summary>
    /// The file lines <paramref name="element"/> occupies, when the document was parsed with
    /// <see cref="LoadOptions.SetLineInfo"/>; null otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From the element's own start to the last line any descendant starts on, or any descendant's
    /// text runs to: a reader reports where elements begin, not where they close, so a closing
    /// tag on a line of its own is left out. Close enough for what it is used for, pinning a review
    /// comment to the element's lines; a closing tag is never what changed.
    /// </para>
    /// <para>
    /// Text counts since 2026-10-23: a vendor's declaration is one text node holding thirty lines
    /// of Structured Text, and a span that ended on its first line left the other twenty-nine
    /// outside the element for review coverage, which then took them for framing between rows.
    /// </para>
    /// </remarks>
    public static LineSpan? LinesOf(XElement element, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element is not IXmlLineInfo self || !self.HasLineInfo())
        {
            return null;
        }

        var end = self.LineNumber;
        foreach (var node in element.DescendantNodes())
        {
            if (node is IXmlLineInfo info && info.HasLineInfo())
            {
                var last = info.LineNumber + (node is XText text ? text.Value.Count(c => c == '\n') : 0);
                end = Math.Max(end, last);
            }
        }

        return new LineSpan(self.LineNumber + offset, end + offset);
    }
}
