using Versio.Core.Iec;
using Versio.Core.Projects;
using Versio.Plc.Codesys.FileBased;
using Versio.Plc.Codesys.Ladder;

namespace Versio.Plc.Codesys;

/// <summary>
/// A CODESYS object's declaration as the neutral member table, from whichever file holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's ask, 2026-10-10: render the way TIA does.</b> A TIA function block shows its
/// interface as a table with the logic underneath. CODESYS had no table at all, so a block's
/// variables read as source text above its code. The table comes from the standard's own syntax
/// (<see cref="StDeclarationTable"/>), and this file only finds the declaration in each of
/// CODESYS's containers:
/// <list type="bullet">
/// <item>A File-Based Storage text file: the lines between its <c>(* METADATA *)</c> header and
/// the end of its declaration (<see cref="CodesysFileBasedSource"/>).</item>
/// <item>A graphical POU's native XML, classic or File-Based Storage: the declaration text the
/// object carries in its <c>Interface</c>. Only when the body is the LD/FBD network list, so the
/// pane can draw the table over the ladder. A CFC body is not drawn yet, and a table over a
/// thousand lines of XML would bury the one thing the table is for.</item>
/// <item>A declaration-only object kept as archive XML — a persistent variable list, a parameter
/// list: the same <c>Interface</c> text, with no implementation under it (2026-10-23).</item>
/// <item>The classic mirror's <c>.st</c>: Versio's own format, read by the neutral
/// <see cref="StDeclarationTable.FromSource"/>.</item>
/// </list>
/// </para>
/// </remarks>
public static class CodesysMemberTable
{
    public static MemberTable? TryParse(string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        if (CodesysFileBasedSource.Sections(relativePath, content) is { } sections)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var declaration = string.Join('\n', lines[sections.HeaderEnd..sections.DeclarationEnd]);
            // The declaration starts after the header: rows name file lines (docs/web/PLAN.md, C3).
            return StDeclarationTable.Parse(declaration, CodesysV3File.ObjectName(relativePath), sections.HeaderEnd + 1);
        }

        if (relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(CodesysV3File.Extension, StringComparison.OrdinalIgnoreCase))
        {
            // Not declarations, but lists a reviewer reads as a table all the same.
            if ((CodesysCatalogs.TextList(relativePath, content)
                 ?? CodesysCatalogs.ProjectSettings(relativePath, content)
                 ?? CodesysCatalogs.SymbolConfiguration(relativePath, content)
                 ?? CodesysCatalogs.SymbolSet(relativePath, content)) is { } list)
            {
                return list;
            }

            if (CodesysLadderParser.ObjectOf(relativePath, content) is not var (owner, name))
            {
                return null;
            }

            var blob = CodesysLadderParser.Named(
                CodesysLadderParser.Named(CodesysLadderParser.Named(owner, "Interface"), "TextDocument"),
                "TextBlobForSerialisation");
            // The declaration text begins on its element's own line; an XML entity never adds a
            // line, so the rows' lines are the file's from there on.
            var first = blob is null ? null : Versio.Core.Text.SafeXml.LinesOf(blob, CodesysLadderParser.LineOffset(relativePath, content));

            if (CodesysLadderParser.IsNetworkList(owner))
            {
                return StDeclarationTable.Parse(blob?.Value, name, first?.Start ?? 1);
            }

            // A declaration with no implementation at all: a persistent variable list
            // (PersistentVars.varpersistent.xml.v3) or a parameter list, which CODESYS keeps as
            // archive XML rather than as a .gvl text file. The IDE shows them as the same
            // declaration a GVL is, and they drew as raw XML (the owner, 2026-10-23).
            //
            // A visualisation also carries an Interface with no Implementation. Its text is a
            // bare VAR_IN_OUT block with no object keyword, which the parser does not table, so
            // a screen stays raw rather than showing a few parameters where its elements should
            // be. A guard here on the table's kind was tried and could never fire;
            // A_visualisation_with_parameters_is_still_not_a_table pins the behaviour instead.
            return blob is not null && CodesysLadderParser.Named(owner, "Implementation") is null
                ? StDeclarationTable.Parse(blob.Value, name, first?.Start ?? 1)
                : null;
        }

        return StDeclarationTable.FromSource(relativePath, content);
    }

    /// <summary>
    /// The code body under the table: a File-Based Storage file's, by its syntax, or the lines
    /// after the delimiter of Versio's own <c>.st</c>.
    /// </summary>
    public static (int Start, int End)? CodeBody(string relativePath, string content) =>
        CodesysFileBasedSource.CodeBody(relativePath, content)
        ?? (relativePath.EndsWith(".st", StringComparison.OrdinalIgnoreCase) ? StDeclarationTable.SourceCodeBody(content) : null);
}
