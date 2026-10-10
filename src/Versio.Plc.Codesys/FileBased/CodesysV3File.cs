using System.Globalization;
using System.Text.RegularExpressions;

namespace Versio.Plc.Codesys.FileBased;

/// <summary>
/// The file conventions of a CODESYS File-Based Storage project: the <c>.xml.v3</c> envelope
/// and the escaped object names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not read from the whitepaper.</b> Everything here was read off the committed
/// fixture <c>fixtures/projects/RefrigeratorControl_FBS/</c> (CODESYS 3.5.22.40, FBS 1.0.0.0).
/// The 0.9 whitepaper describes dividers that 1.0 no longer writes, so it is not a source for
/// the format. See <c>docs/fbs/PLAN.md</c> §2.
/// </para>
/// <para>
/// <b>The envelope.</b> Every object that has no textual form is written as
/// <c>__V3_META__</c>, a JSON header naming its GUID and type GUID, <c>__V3_CONTENT__</c>, and
/// then the object's own archive XML. It is the same XML a classic <c>export_native</c> writes for
/// that object, <b>without</b> the <c>ExportFile/StructuredView/EntryList</c> wrapper around it.
/// That is why a parser of the native XML can read either file once it is handed the element.
/// </para>
/// </remarks>
public static partial class CodesysV3File
{
    /// <summary>The suffix of every non-textual object file.</summary>
    public const string Extension = ".xml.v3";

    private const string MetaMarker = "__V3_META__";
    private const string ContentMarker = "__V3_CONTENT__";

    /// <summary>
    /// The XML after the envelope's header, or null when <paramref name="content"/> is not an
    /// enveloped file.
    /// </summary>
    /// <remarks>
    /// The header must come first. A file that merely mentions the marker somewhere in its
    /// text is not one, and reading it from the middle would hand a parser half a document.
    /// </remarks>
    public static string? Content(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = content.TrimStart('\uFEFF');
        if (!text.StartsWith(MetaMarker, StringComparison.Ordinal))
        {
            return null;
        }

        var at = text.IndexOf(ContentMarker, StringComparison.Ordinal);
        return at < 0 ? null : text[(at + ContentMarker.Length)..].TrimStart('\r', '\n');
    }

    /// <summary>
    /// The object's name as the IDE shows it, from its file name: <c>Signals.pou.xml.v3</c> is
    /// <c>Signals</c>, and <c>__3cEmpty__3e 0.slotdevice.xml.v3</c> is <c>&lt;Empty&gt; 0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An object name is an IEC identifier or a free-text name with no dot in either case, so the
    /// first dot ends the name and everything after it is the kind and the storage suffix.
    /// </para>
    /// <para>
    /// Only the escape the fixture shows is decoded: <c>__</c> and two lower-case hex digits,
    /// one byte. A name that needs anything wider (a non-ASCII character, say) is left as it is
    /// written until a specimen shows how CODESYS spells it. Guessing would show a reviewer a
    /// name the IDE does not.
    /// </para>
    /// </remarks>
    public static string ObjectName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = file.IndexOf('.', StringComparison.Ordinal);
        return Decode(dot <= 0 ? file : file[..dot]);
    }

    /// <summary>A name with its <c>__xx</c> escapes turned back into the characters they stand for.</summary>
    public static string Decode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Escape().Replace(name, m =>
            ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
    }

    // ASCII only (00-7f): a byte above that is half of some multi-byte character, and decoding
    // it alone would print a replacement character where the IDE prints a letter.
    [GeneratedRegex("__([0-7][0-9a-f])")]
    private static partial Regex Escape();
}
