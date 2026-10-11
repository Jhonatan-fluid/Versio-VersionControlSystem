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

    /// <summary>Parses <paramref name="xml"/>, or throws <see cref="XmlException"/>.</summary>
    /// <param name="maxCharacters">The cap; <see cref="MaxCharacters"/> unless a test needs a small one.</param>
    public static XDocument Parse(string xml, LoadOptions options = LoadOptions.None, long maxCharacters = MaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var settings = new XmlReaderSettings
        {
            // Mirrors what XDocument.Parse sets for itself (whitespace below), so the trees are
            // the same; only the DTD and the cap differ.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            IgnoreWhitespace = (options & LoadOptions.PreserveWhitespace) == 0,
        };

        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, settings);
        return XDocument.Load(reader, options);
    }
}
