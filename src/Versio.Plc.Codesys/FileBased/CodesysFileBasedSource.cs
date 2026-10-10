using Versio.Core.Diff;
using Versio.Core.Iec;

namespace Versio.Plc.Codesys.FileBased;

/// <summary>
/// How a CODESYS File-Based Storage text file is read: where its header, declaration and code
/// body are, and which of its extensions are Structured Text.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no divider to find.</b> The 0.9 whitepaper describes <c>__DECLARATION__</c> and
/// <c>__METADATA__</c> markers, and 1.0 dropped them. A file now holds a <c>(* METADATA {…} *)</c>
/// header, then the object as the IDE shows it, then its closing keyword. The split has to come
/// from IEC syntax, and four cases in the fixture show what that has to handle (see
/// <c>docs/fbs/PLAN.md</c> §2):
/// <list type="bullet">
/// <item>A body after <c>END_VAR</c> (<c>PLC_PRG.prg.st</c>): the declaration runs to the last
/// <c>END_VAR</c>.</item>
/// <item>No <c>VAR</c> block at all (<c>M_Reset.meth.st</c>): the declaration is the attribute
/// lines and the <c>METHOD</c> line.</item>
/// <item>The closing keyword on a code line (<c>P_Active := …;END_PROPERTY</c>): the line stays
/// whole. Cutting text out of a line would hide code from the reviewer.</item>
/// <item>A type or a global variable list: declaration only, no body.</item>
/// </list>
/// </para>
/// <para>
/// <b>Only a file that opens with the header is answered for.</b> A classic mirror's
/// <c>.st</c> is Versio's own format with its own delimiter, and must keep the layout it has.
/// </para>
/// </remarks>
public static class CodesysFileBasedSource
{
    private const string HeaderOpen = "(* METADATA";
    private const string HeaderClose = "*)";

    /// <summary>The declaration-only objects: their whole text is what the IDE's declaration pane shows.</summary>
    private static readonly string[] DeclarationOnly = ["TYPE", "VAR_GLOBAL", "NAMESPACE", "INTERFACE"];

    /// <summary>The objects that have a code body under their declaration.</summary>
    private static readonly string[] WithBody =
        ["PROGRAM", "FUNCTION_BLOCK", "FUNCTION", "METHOD", "PROPERTY", "ACTION", "TRANSITION"];

    /// <summary>
    /// The extensions that hold Structured Text without saying <c>.st</c>. Seen in the fixture
    /// except the last three, which the whitepaper names.
    /// </summary>
    private static readonly string[] TextExtensions = [".gvl", ".enum", ".struct", ".union", ".alias", ".itf"];

    public static SourceSections? Sections(string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrEmpty(content) || !IsText(relativePath))
        {
            return null;
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (!lines[0].TrimStart('\uFEFF').StartsWith(HeaderOpen, StringComparison.Ordinal))
        {
            return null;
        }

        var close = Array.FindIndex(lines, l => l.Trim() == HeaderClose);
        if (close < 0)
        {
            return null;
        }

        var header = close + 1;

        // The object's keyword: the first line after the header that is not an attribute, a
        // comment or blank. It decides whether there is a body to look for at all.
        var keywordLine = -1;
        for (var i = header; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0 || text.StartsWith('{') || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            keywordLine = i;
            break;
        }

        if (keywordLine < 0)
        {
            return null;
        }

        var keyword = Keyword(lines[keywordLine]);
        if (DeclarationOnly.Contains(keyword, StringComparer.OrdinalIgnoreCase))
        {
            return new SourceSections(header, lines.Length, BodyEnd: null);
        }

        if (!WithBody.Contains(keyword, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        // The object's own closing keyword (END_PROGRAM for a PROGRAM), when it stands alone on
        // the last line with text. Glued to code it is part of a line of code, and the line is
        // drawn whole. Matched exactly rather than as "any END_": a body whose last line is a
        // bare END_IF, with the closing keyword glued on elsewhere, must not lose its END_IF.
        var last = Array.FindLastIndex(lines, l => l.Trim().Length > 0);
        var bodyEnd = lines[last].Trim().Equals("END_" + keyword, StringComparison.OrdinalIgnoreCase)
            ? last
            : lines.Length;

        var endVar = Array.FindLastIndex(lines, bodyEnd - 1, bodyEnd - keywordLine,
            l => l.Trim().StartsWith("END_VAR", StringComparison.OrdinalIgnoreCase));
        var declarationEnd = endVar >= 0 ? endVar + 1 : keywordLine + 1;

        return new SourceSections(header, declarationEnd, bodyEnd);
    }

    /// <summary>The code body's lines, for drawing a block's code under its interface table.</summary>
    public static (int Start, int End)? CodeBody(string relativePath, string content) =>
        Sections(relativePath, content) is { BodyEnd: { } end } sections ? (sections.DeclarationEnd, end) : null;

    /// <summary>Structured Text for the File-Based Storage extensions that do not end in <c>.st</c>.</summary>
    public static SyntaxLanguage? Syntax(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return TextExtensions.Any(e => relativePath.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            ? LanguageCatalog.StructuredText
            : relativePath.EndsWith(CodesysV3File.Extension, StringComparison.OrdinalIgnoreCase)
                ? LanguageCatalog.Xml
                : null;
    }

    private static bool IsText(string path) =>
        path.EndsWith(".st", StringComparison.OrdinalIgnoreCase)
        || TextExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first word, without a <c>_GET</c>/<c>_SET</c> accessor suffix: <c>PROPERTY_Get</c> is a property.</summary>
    private static string Keyword(string line)
    {
        var word = line.Trim().Split([' ', '\t'], 2)[0];
        var underscore = word.IndexOf("_GET", StringComparison.OrdinalIgnoreCase);
        underscore = underscore < 0 ? word.IndexOf("_SET", StringComparison.OrdinalIgnoreCase) : underscore;
        return word.StartsWith("PROPERTY", StringComparison.OrdinalIgnoreCase) && underscore > 0 ? word[..underscore] : word;
    }
}
