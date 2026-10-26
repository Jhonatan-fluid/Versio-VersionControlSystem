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
        var document = XDocument.Load(reader, options);

        // Kept so LinesOf can measure a text node against the file rather than against its
        // decoded value — see Source. Only when line info was asked for, which is the only
        // time anything will look.
        if ((options & LoadOptions.SetLineInfo) != 0)
        {
            document.AddAnnotation(new Source(xml));
        }

        return document;
    }

    /// <summary>
    /// The text a document was parsed from, annotated onto it by <see cref="Parse"/>, so a
    /// text node's physical extent can be read from the file instead of guessed from its
    /// decoded value.
    /// </summary>
    /// <remarks>
    /// A reader reports where a text node starts and nothing about where it ends. Counting
    /// the newlines in its value stood in for that until 2026-10-24, and it is wrong whenever
    /// the file spells a newline as <c>&amp;#10;</c> or <c>&amp;#xA;</c>: the value holds a
    /// line break the file does not, so a member's span ran past its own element into the
    /// next member's lines — and <c>Anchors.ByLine</c>, which takes the smallest span holding
    /// a line, could then hand the next member's line to this one (the 2026-10-24 review's
    /// item 18). The file is the only thing that knows which newlines are physical.
    /// </remarks>
    private sealed class Source(string text)
    {
        private int[]? _lineStarts;

        /// <summary>
        /// The character offset of a reader position, or -1 when the position is not in the text.
        /// </summary>
        /// <remarks>
        /// Line breaks are counted as the reader counts them: <c>\n</c>, <c>\r\n</c> and a lone
        /// <c>\r</c> each end a line.
        /// </remarks>
        public int OffsetOf(int line, int position)
        {
            var starts = _lineStarts ??= LineStarts(text);
            if (line < 1 || line > starts.Length || position < 1)
            {
                return -1;
            }

            var offset = starts[line - 1] + position - 1;
            return offset <= text.Length ? offset : -1;
        }

        /// <summary>
        /// The physical lines a text node beginning at <paramref name="offset"/> spans beyond its
        /// first: up to the next <c>&lt;</c> for ordinary text, which cannot hold one, and up to
        /// the closing <c>]]&gt;</c> for a CDATA section, which can.
        /// </summary>
        public int LineBreaksFrom(int offset, bool cdata)
        {
            var end = cdata ? text.IndexOf("]]>", offset, StringComparison.Ordinal) : text.IndexOf('<', offset);
            if (end < 0)
            {
                end = text.Length;
            }

            var breaks = 0;
            for (var i = offset; i < end; i++)
            {
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= end || text[i + 1] != '\n')))
                {
                    breaks++;
                }
            }

            return breaks;
        }

        private static int[] LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
                {
                    starts.Add(i + 1);
                }
            }

            return [.. starts];
        }
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
    /// <para>
    /// Text is measured in the file, not in its value, since 2026-10-24: <c>&amp;#10;</c> is a
    /// line break in the value and none in the file, and counting it ran a member's span into
    /// the next member's lines (see <see cref="Source"/>). A document parsed elsewhere than
    /// <see cref="Parse"/> has no file to measure against and falls back to the value.
    /// </para>
    /// </remarks>
    public static LineSpan? LinesOf(XElement element, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element is not IXmlLineInfo self || !self.HasLineInfo())
        {
            return null;
        }

        var source = element.Document?.Annotation<Source>();
        var end = self.LineNumber;
        foreach (var node in element.DescendantNodes())
        {
            if (node is IXmlLineInfo info && info.HasLineInfo())
            {
                var last = info.LineNumber + (node is XText text ? LineBreaksIn(text, info, source) : 0);
                end = Math.Max(end, last);
            }
        }

        return new LineSpan(self.LineNumber + offset, end + offset);
    }

    /// <summary>True when any newline in <paramref name="text"/>'s value is not a newline in the file.</summary>
    /// <remarks>
    /// For a caller that numbers lines by splitting the decoded value — a vendor's declaration
    /// blob — and must know when that numbering cannot be the file's. Null when the document
    /// has no file to measure against, or the node no line info.
    /// </remarks>
    public static bool? HasEncodedLineBreaks(XText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text is not IXmlLineInfo info || !info.HasLineInfo())
        {
            return null;
        }

        var physical = PhysicalLineBreaks(text, info, text.Document?.Annotation<Source>());
        return physical is null ? null : physical != text.Value.Count(c => c == '\n');
    }

    private static int LineBreaksIn(XText text, IXmlLineInfo info, Source? source) =>
        PhysicalLineBreaks(text, info, source) ?? text.Value.Count(c => c == '\n');

    /// <summary>The line breaks the file holds inside this text node, or null when there is no file to read.</summary>
    private static int? PhysicalLineBreaks(XText text, IXmlLineInfo info, Source? source)
    {
        if (source is null)
        {
            return null;
        }

        var offset = source.OffsetOf(info.LineNumber, info.LinePosition);
        return offset < 0 ? null : source.LineBreaksFrom(offset, text is XCData);
    }
}
