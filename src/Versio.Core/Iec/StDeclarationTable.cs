using System.Text;
using Versio.Core.Projects;
using Versio.Core.Text;

namespace Versio.Core.Iec;

/// <summary>
/// An IEC 61131-3 declaration (a block's interface, a structure, an enumeration, a global variable
/// list) read into the vendor-neutral <see cref="MemberTable"/> the pane already draws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it lives here.</b> TIA Portal hands Versio its interfaces as XML, so its pack reads them
/// into a table itself. Every vendor that writes Structured Text (CODESYS in its mirror and in
/// File-Based Storage, TwinCAT) writes the same <c>VAR_INPUT … END_VAR</c> blocks, because the
/// syntax is the standard's. One reader here gives them all the table TIA's blocks get, which is
/// the point: the owner asked for a function block to read the same way on every platform, with
/// its variables as a table on top and its logic underneath (2026-10-10).
/// </para>
/// <para>
/// <b>Sections in TIA's words.</b> The pane already shows a TIA interface grouped as Input,
/// Output, InOut, Static, Temp, Constant and Return, and a reviewer moving between platforms
/// should meet the same headings. So <c>VAR_INPUT</c> is "Input", a plain <c>VAR</c> is "Static"
/// in a program or function block (it keeps its value between calls) and "Temp" in a function or
/// method (it does not), and <c>RETAIN</c> / <c>PERSISTENT</c> are said after the section's name
/// rather than lost. A function's or method's return value is a "Return" row under its own name,
/// because in Structured Text the result is assigned to the block's name.
/// </para>
/// <para>
/// <b>Comments the way the IDE pairs them.</b> A trailing comment belongs to its declaration; with
/// none, the comment lines directly above it do. A blank line breaks that, so a heading comment
/// separated by a blank line from the variables under it is not given to the first of them.
/// </para>
/// <para>
/// <b>What it does not do.</b> It reads; it never writes back. It does not resolve types: a
/// non-elementary type goes in <see cref="MemberRow.TypeReference"/> for the host to look up. An
/// object kind it does not table (an interface, an alias) returns null, and the file keeps its
/// source view.
/// </para>
/// </remarks>
public static partial class StDeclarationTable
{
    private static readonly HashSet<string> Elementary = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT", "USINT", "UINT",
        "UDINT", "ULINT", "REAL", "LREAL", "TIME", "LTIME", "DATE", "LDATE", "TIME_OF_DAY", "TOD",
        "LTOD", "LTIME_OF_DAY", "DATE_AND_TIME", "DT", "LDT", "LDATE_AND_TIME", "STRING", "WSTRING",
        "CHAR", "WCHAR", "BIT", "ANY", "ANY_NUM", "ANY_INT", "ANY_REAL", "ANY_BIT",
    };

    private static readonly string[] Modifiers =
        ["PUBLIC", "PRIVATE", "PROTECTED", "INTERNAL", "FINAL", "ABSTRACT"];

    /// <summary>The kinds of object whose declaration is a table.</summary>
    private enum Kind { Unknown, Block, Function, Structure, Enumeration, VariableList }

    /// <summary>
    /// The table for one declaration, or null when it is not an object this reader tables.
    /// </summary>
    /// <param name="declaration">The declaration part only: from the header to the last <c>END_VAR</c> or <c>END_TYPE</c>.</param>
    /// <param name="fallbackName">
    /// The name to use when the text does not say one, as a global variable list does not.
    /// </param>
    /// <param name="firstLine">
    /// The file line the declaration's first line is, from 1: rows carry file lines
    /// (<see cref="MemberRow.Lines"/>), and a declaration is often read out of a larger file — after a
    /// vendor's header, or out of an XML element.
    /// </param>
    public static MemberTable? Parse(string? declaration, string fallbackName, int firstLine = 1)
    {
        var table = ParseLines(declaration, fallbackName);
        return table is null ? null : table with
        {
            Members = table.Members.Select(r => r with
            {
                Lines = r.Lines?.Shift(firstLine),
                Attributes = Shifted(r.Attributes, firstLine),
            }).ToList(),
            Attributes = Shifted(table.Attributes, firstLine),
            Unshown = table.Unshown.Select(u => u with { Line = u.Line + firstLine }).ToList(),
            Structural = table.Structural.Select(s => s.Shift(firstLine)).ToList(),
        };
    }

    private static IReadOnlyList<MemberAttribute> Shifted(IReadOnlyList<MemberAttribute> attributes, int lines) =>
        attributes.Count == 0 ? attributes : attributes.Select(a => a with { Lines = a.Lines?.Shift(lines) }).ToList();

    /// <summary><see cref="Parse"/>, with each row's lines as zero-based indexes into the declaration.</summary>
    private static MemberTable? ParseLines(string? declaration, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(declaration))
        {
            return null;
        }

        var lines = Clean(declaration);
        var header = lines.FindIndex(l => l.Code.Length > 0);
        if (header < 0)
        {
            return null;
        }

        // Every line a row, a heading or an attribute accounts for. Whatever is left is what
        // the table does not draw, and a comparison warns when that changes (UnshownChange).
        var claimed = new HashSet<int>();
        var words = Words(lines[header].Code);
        var first = words[0].ToUpperInvariant();
        var table = first switch
        {
            "PROGRAM" or "FUNCTION_BLOCK" => Block(lines, header, words, Kind.Block, claimed),
            "FUNCTION" or "METHOD" => Block(lines, header, words, Kind.Function, claimed),
            _ when first.StartsWith("PROPERTY", StringComparison.Ordinal) => Block(lines, header, words, Kind.Function, claimed),
            "TYPE" => Type(lines, header, words, claimed),
            "VAR_GLOBAL" or "NAMESPACE" => Variables(lines, header, words, fallbackName, claimed),
            _ => null,
        };

        if (table is null)
        {
            return null;
        }

        // The object's own attributes are the ones written above its header, or on the header
        // line itself. CODESYS writes them there for every kind of object; TwinCAT the same.
        var attributes = new List<MemberAttribute>();
        for (var i = 0; i <= header; i++)
        {
            if (lines[i].Attributes.Count > 0)
            {
                attributes.AddRange(lines[i].Attributes.Select(a => a with { Lines = new LineSpan(i, i) }));
                claimed.Add(i);
            }
        }

        // A block comment's text is given to the line that closes it, so the lines it opened on
        // are drawn wherever that line is: claimed with it, or an edit to the middle of a row's
        // multi-line comment would warn that it is not shown when it is.
        foreach (var i in claimed.ToList())
        {
            for (var k = i - 1; k >= 0 && lines[k].Open; k--)
            {
                claimed.Add(k);
            }
        }

        var unshown = new List<UnshownLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Text.Length > 0 && !claimed.Contains(i))
            {
                unshown.Add(new UnshownLine(i, lines[i].Text));
            }
        }

        // The header is shown in part (the name in the title, a function's type as its Return
        // row, its kind as the table's kind) and in part not (EXTENDS, IMPLEMENTS, a comment).
        // The part that is not stands in for the line, so a changed EXTENDS warns and a changed
        // return type does not warn twice. A header with no such part is framing (below).
        var residue = HeaderResidue(lines, header, words, first);
        if (residue is not null)
        {
            unshown.Add(new UnshownLine(header, residue));
            unshown.Sort((a, b) => a.Line.CompareTo(b.Line));
        }

        // The framing: every line claimed by the declaration's own syntax rather than by a row or
        // an attribute (the header when it says nothing the table does not draw, VAR … END_VAR,
        // STRUCT, END_TYPE), and every blank line. Review coverage vouches for a change there —
        // the owner's real pull request (2026-10-23) listed a VAR_IN_OUT block opened, a blank
        // line removed and a method header that gained PUBLIC as "changed, but not shown", and
        // Raw had nothing more to say about any of them than the table did.
        var drawn = new HashSet<int>();
        foreach (var span in table.Members.Select(r => r.Lines).Concat(table.Members.SelectMany(r => r.Attributes).Select(a => a.Lines))
                     .Concat(attributes.Select(a => a.Lines)).OfType<LineSpan>())
        {
            drawn.UnionWith(Enumerable.Range(span.Start, span.Length));
        }

        var structural = Enumerable.Range(0, lines.Count)
            .Where(i => (lines[i].Text.Length == 0 || (claimed.Contains(i) && !drawn.Contains(i))) && !(i == header && residue is not null))
            .ToList();

        return table with { Attributes = attributes, Unshown = unshown, Structural = Spans(structural) };
    }

    /// <summary>Line indexes folded into spans, so a table carries a few ranges rather than a line list.</summary>
    private static IReadOnlyList<LineSpan> Spans(IReadOnlyList<int> lines)
    {
        var spans = new List<LineSpan>();
        foreach (var line in lines.Order())
        {
            if (spans.Count > 0 && spans[^1].End == line - 1)
            {
                spans[^1] = spans[^1] with { End = line };
            }
            else
            {
                spans.Add(new LineSpan(line, line));
            }
        }

        return spans;
    }

    /// <summary>
    /// What a block's header line says that the table does not draw, or null when it is all
    /// drawn (or the header is not a block's, whose lines the kinds claim themselves).
    /// </summary>
    /// <remarks>
    /// The keyword and the modifiers are not part of it, since 2026-10-23. The keyword is the
    /// table's kind, which is drawn; a modifier (<c>PUBLIC</c>, <c>ABSTRACT</c>) is not drawn,
    /// and is not reported either: on the owner's real pull request a method header that gained
    /// <c>PUBLIC</c> made its empty table say "1 declaration change is not shown" beside a code
    /// block that showed every change, and the owner's rule is that the header line is framing.
    /// The cost, stated: an access modifier changed from one value to another is not drawn and
    /// not warned about. What remains is what says something the table has no cell for — an
    /// <c>EXTENDS</c>, an <c>IMPLEMENTS</c>, the line's comment.
    /// </remarks>
    private static string? HeaderResidue(List<Line> lines, int header, List<string> words, string first)
    {
        if (first is "TYPE" or "VAR_GLOBAL" or "NAMESPACE")
        {
            return null;
        }

        var function = first is "FUNCTION" or "METHOD" || first.StartsWith("PROPERTY", StringComparison.Ordinal);
        // By word, not by IndexOf: "B" is inside "FUNCTION_BLOCK".
        var at = words.FindIndex(1, w => !Modifiers.Contains(w, StringComparer.OrdinalIgnoreCase));
        if (function || at < 0)
        {
            // The type and the line's comment are the Return row; the keywords are the kind.
            return null;
        }

        var after = string.Join(' ', words.Skip(at + 1));
        var comment = lines[header].Comment;
        var residue = Collapse($"{after}{(comment.Length > 0 ? " // " + comment : "")}");
        return residue.Length == 0 ? null : residue;
    }

    /// <summary>
    /// The table for one of Versio's own <c>.st</c> files: its declaration half read by
    /// <see cref="Parse"/>, named after the file. Null for any other file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>.st</c> mirror is Versio's format, not a vendor's: every pack that exports
    /// Structured Text writes it with <see cref="StTextCodec.ImplementationDelimiter"/> between
    /// the halves. So reading it lives here, and each such pack's format calls it. Asking every
    /// format, the pane gets the same table whichever answers first.
    /// </para>
    /// <para>
    /// The name is the file's up to its first dot (<c>Glob_Var.gvl.st</c> is <c>Glob_Var</c>),
    /// used when the declaration does not say one, as a global variable list does not.
    /// </para>
    /// </remarks>
    public static MemberTable? FromSource(string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(content)
            || !relativePath.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var file = relativePath[(relativePath.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = file.IndexOf('.', StringComparison.Ordinal);
        var declaration = content.Contains(StTextCodec.ImplementationDelimiter, StringComparison.Ordinal)
            ? StTextCodec.Split(content).Declaration
            : content;
        return Parse(declaration, dot > 0 ? file[..dot] : file);
    }

    /// <summary>
    /// The code body of one of Versio's own <c>.st</c> files: every line after the delimiter, or
    /// null when there is none (a type, a list, a block with no code).
    /// </summary>
    /// <remarks>
    /// Only a delimiter that is the whole of its line counts, the rule
    /// <see cref="StSourceLayout"/> draws by, so the code under a table and the code in the
    /// Rendered view start on the same line.
    /// </remarks>
    public static (int Start, int End)? SourceCodeBody(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = content.Replace("\r\n", "\n").Split('\n');
        // The first occurrence decides, as it does in StTextCodec.Split and StSourceLayout.
        var at = Array.FindIndex(lines, l => l.Contains(StTextCodec.ImplementationDelimiter, StringComparison.Ordinal));
        return at < 0 || lines[at].Trim() != StTextCodec.ImplementationDelimiter ? null : (at + 1, lines.Length);
    }

    /// <summary>A program, function block, function, method or property: sections with headings.</summary>
    private static MemberTable? Block(List<Line> lines, int header, List<string> words, Kind kind, HashSet<int> claimed)
    {
        var rest = words.Skip(1).Where(w => !Modifiers.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
        if (rest.Count == 0)
        {
            return null;
        }

        var name = rest[0].TrimEnd(':');
        var rows = new List<MemberRow>();

        // "FUNCTION F : INT" / "METHOD M : BOOL" / "PROPERTY P : REAL": the result, under the
        // block's own name, which is how Structured Text assigns it.
        var colon = lines[header].Code.IndexOf(':', StringComparison.Ordinal);
        var returnRow = false;
        if (kind == Kind.Function && colon >= 0)
        {
            var type = UntilKeyword(lines[header].Code[(colon + 1)..].Trim());
            if (type.Length > 0)
            {
                AddHeading(rows, "Return");
                rows.Add(Row(name, type, "", lines[header].Comment, "Return", new LineSpan(header, header)));
                returnRow = true;
            }
        }

        // The header line stands as its residue (HeaderResidue), not as itself — except a
        // function's or method's with a comment and no Return row to draw the comment in: a
        // residue is not made for those kinds, so the comment was framing and a change to it
        // went unreported (review, 2026-10-24). Unclaimed, the line is listed as not shown.
        if (kind != Kind.Function || returnRow || lines[header].Comment.Length == 0)
        {
            claimed.Add(header);
        }

        string? section = null;
        foreach (var d in Declarations(
                     lines, header + 1, line => SectionOf(line, kind) is not null || IsEnd(line, "END_VAR")))
        {
            if (d.Structural is not null)
            {
                // Closed by END_VAR: anything before the next VAR is not a variable.
                section = SectionOf(d.Structural, kind);
                ClaimKeywordLines(lines, d, claimed);
                continue;
            }

            if (section is null)
            {
                // Not drawn, so not claimed: a declaration outside any section is unshown.
                continue;
            }

            if (!rows.Any(r => r.IsSectionHeading && r.Section == section))
            {
                AddHeading(rows, section);
            }

            var made = Rows(d, section).ToList();
            if (made.Count > 0)
            {
                // A declaration with no type draws no row, so its lines stay unshown.
                claimed.UnionWith(d.Claimed);
                rows.AddRange(made);
            }
        }

        return new MemberTable { Name = name, Members = rows, IsInterface = true };
    }

    /// <summary>A structure, union or enumeration: one row per member, no headings.</summary>
    private static MemberTable? Type(List<Line> lines, int header, List<string> words, HashSet<int> claimed)
    {
        if (words.Count < 2)
        {
            return null;
        }

        var name = words[1].TrimEnd(':');
        var body = string.Join(' ', lines.Skip(header).Select(l => l.Code)).Trim();
        var afterColon = body.IndexOf(':', StringComparison.Ordinal) is var c and >= 0 ? body[(c + 1)..].TrimStart() : "";

        if (afterColon.StartsWith('('))
        {
            return Enumeration(lines, header, name, afterColon, claimed);
        }

        var opener = afterColon.StartsWith("STRUCT", StringComparison.OrdinalIgnoreCase) ? "STRUCT"
            : afterColon.StartsWith("UNION", StringComparison.OrdinalIgnoreCase) ? "UNION"
            : null;
        if (opener is null)
        {
            // An alias (TYPE Temperature : REAL; END_TYPE) is one line of source. A one-row
            // table would say less than the line does.
            return null;
        }

        var start = lines.FindIndex(header, l => Words(l.Code).Any(w => w.Equals(opener, StringComparison.OrdinalIgnoreCase)));
        var rows = new List<MemberRow>();
        var end = lines.Count;
        foreach (var d in Declarations(lines, start + 1, line => IsEnd(line, "END_" + opener)))
        {
            if (d.Structural is not null)
            {
                end = d.First;
                break;
            }

            var made = Rows(d, "").ToList();
            if (made.Count > 0)
            {
                claimed.UnionWith(d.Claimed);
                rows.AddRange(made);
            }
        }

        // "TYPE X :" and "STRUCT" are the title and the kind; END_STRUCT and END_TYPE close
        // them. Anything else in those lines (a STRUCT EXTENDS) is not drawn, so the header
        // lines are claimed only when they say nothing more than that.
        for (var i = header; i <= start; i++)
        {
            var said = Words(lines[i].Code).Where(w => !w.Equals("TYPE", StringComparison.OrdinalIgnoreCase)
                && !w.Equals(opener, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(w.TrimEnd(':'), name, StringComparison.Ordinal) && w != ":");
            if (!said.Any() && lines[i].Comment.Length == 0)
            {
                claimed.Add(i);
            }
        }

        for (var i = end; i < lines.Count; i++)
        {
            if (IsEnd(lines[i].Code, "END_" + opener) || IsEnd(lines[i].Code, "END_TYPE"))
            {
                ClaimKeywordLine(lines, i, claimed);
            }
        }

        return new MemberTable { Name = name, Members = rows, IsDataType = true };
    }

    /// <summary>
    /// A keyword line (<c>VAR_INPUT</c>, <c>END_VAR</c>, <c>END_STRUCT</c>) is the declaration's
    /// own syntax and framing — until it carries a comment, which is drawn nowhere: then the line
    /// stays unclaimed and is reported as not shown. The STRUCT line always checked its comment;
    /// the section and end lines did not (review, 2026-10-24).
    /// </summary>
    private static void ClaimKeywordLine(List<Line> lines, int index, HashSet<int> claimed)
    {
        if (lines[index].Comment.Length == 0)
        {
            claimed.Add(index);
        }
    }

    /// <summary>
    /// A section line's claim, as <see cref="Declarations"/> handed it back: its line when the
    /// keyword is all the line holds, nothing when a declaration shares the line. That line is
    /// the row's to claim — or nobody's, and then it is reported as not shown. The keyword claimed
    /// its whole line regardless until the 2026-10-25 review's item 9, so <c>VAR x INT; END_VAR</c>
    /// drew no row and was framing, and the lost declaration was reported nowhere.
    /// </summary>
    private static void ClaimKeywordLines(List<Line> lines, Declaration structural, HashSet<int> claimed)
    {
        foreach (var index in structural.Claimed)
        {
            ClaimKeywordLine(lines, index, claimed);
        }
    }

    /// <summary>
    /// An enumeration's values: the name, the value it is given (as the default-value column), and
    /// the base type after the closing parenthesis as each row's type, as the IDE shows them.
    /// </summary>
    private static MemberTable Enumeration(List<Line> lines, int header, string name, string afterColon, HashSet<int> claimed)
    {
        // Claimed line by line: a value's line and the comment lines a value draws, the opener,
        // the closer with the base type, and END_TYPE. Not a pragma on a value, not a comment no
        // value took, not a comment on the header: those are drawn nowhere, and once review
        // coverage trusted the claims (2026-10-23) "claim every line" made a changed pragma read
        // as shown (review, 2026-10-24).
        ClaimKeywordLine(lines, header, claimed);

        var close = afterColon.LastIndexOf(')');
        var baseType = close >= 0
            ? afterColon[(close + 1)..].Replace("END_TYPE", "", StringComparison.OrdinalIgnoreCase).Trim().Trim(';').Trim()
            : "";

        var rows = new List<MemberRow>();
        var depth = 0;
        var pending = new List<string>();
        var pendingLines = new List<int>();
        for (var i = header; i < lines.Count; i++)
        {
            var line = lines[i];
            var text = new StringBuilder();
            foreach (var ch in line.Code)
            {
                if (ch == '(')
                {
                    depth++;
                    if (depth == 1)
                    {
                        continue;
                    }
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                if (depth >= 1)
                {
                    text.Append(ch);
                }
            }

            var items = text.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (items.Length == 0 && line.Code.Length == 0 && line.Comment.Length > 0)
            {
                pending.Add(line.Comment);
                pendingLines.Add(i);
                continue;
            }

            foreach (var item in items)
            {
                var assign = item.IndexOf(":=", StringComparison.Ordinal);
                var member = assign >= 0 ? item[..assign].Trim() : item.Trim();
                var value = assign >= 0 ? item[(assign + 2)..].Trim() : "";
                var comment = line.Comment.Length > 0 ? line.Comment : string.Join(' ', pending);
                rows.Add(Row(member, baseType, value, comment, "", new LineSpan(i, i)) with { TypeReference = "" });
                pending.Clear();
            }

            if (items.Length > 0)
            {
                // A value's line is drawn with its comment; the comment lines above it are drawn in
                // its cell. Those are the row's.
                claimed.Add(i);
                claimed.UnionWith(pendingLines);
                pendingLines.Clear();
            }
            else if (i > header && line.Code.Length > 0 && line.Comment.Length == 0)
            {
                // The opener, or the closer with the base type: syntax, drawn as the table's kind.
                claimed.Add(i);
            }

            if (line.Code.Contains(')', StringComparison.Ordinal) && depth == 0 && i > header)
            {
                for (var k = i + 1; k < lines.Count; k++)
                {
                    if (IsEnd(lines[k].Code, "END_TYPE"))
                    {
                        ClaimKeywordLine(lines, k, claimed);
                    }
                }

                break;
            }
        }

        return new MemberTable { Name = name, Members = rows, IsDataType = true };
    }

    /// <summary>
    /// A global variable list. Headings only when it has more than one kind of block (a
    /// <c>VAR_GLOBAL CONSTANT</c> beside a <c>VAR_GLOBAL</c>): a single heading over every row says
    /// nothing the title does not.
    /// </summary>
    private static MemberTable? Variables(List<Line> lines, int header, List<string> words, string fallbackName, HashSet<int> claimed)
    {
        var name = words[0].Equals("NAMESPACE", StringComparison.OrdinalIgnoreCase) && words.Count > 1
            ? words[1]
            : fallbackName;

        var found = new List<(string Section, Declaration Declaration)>();
        static string First(string line) => Words(line).FirstOrDefault()?.TrimEnd(';').ToUpperInvariant() ?? "";

        string? section = null;
        foreach (var d in Declarations(
                     lines, header, line => First(line) is "VAR_GLOBAL" or "END_VAR" or "NAMESPACE" or "END_NAMESPACE"))
        {
            if (d.Structural is not null)
            {
                section = First(d.Structural) == "VAR_GLOBAL" ? Qualified("Global", Words(d.Structural).Skip(1)) : null;
                ClaimKeywordLines(lines, d, claimed);
                continue;
            }

            if (section is not null && Rows(d, section).Any())
            {
                claimed.UnionWith(d.Claimed);
                found.Add((section, d));
            }
        }

        var headed = found.Select(f => f.Section).Distinct().Count() > 1;
        var rows = new List<MemberRow>();
        foreach (var (s, d) in found)
        {
            if (headed && !rows.Any(r => r.IsSectionHeading && r.Section == s))
            {
                AddHeading(rows, s);
            }

            rows.AddRange(Rows(d, s));
        }

        return name.Length == 0 ? null : new MemberTable { Name = name, Members = rows, IsVariableList = true };
    }

    /// <summary>
    /// Every declaration from <paramref name="from"/> on, each with its comment, and in their
    /// place every line <paramref name="isStructural"/> claims (a section opening or closing), as
    /// <c>Structural</c> with no declaration.
    /// </summary>
    /// <remarks>
    /// The structural lines come back in order, as events, rather than being acted on in a
    /// callback, because order is the point: a declaration still open when END_VAR arrives (one
    /// missing its semicolon) is handed back first, under the section it was written in, and
    /// only then does the caller close the section. Acting in the callback switched the section
    /// before the flushed row was seen, and the row landed under the next one.
    /// <para>
    /// Each comes with the zero-based indexes of its first and last line, so a row can say where
    /// it was declared (<see cref="MemberRow.Lines"/>).
    /// </para>
    /// <para>
    /// <b>Attributes wait for the next declaration</b>, as pending comments do, but a blank line
    /// does not drop them: the compiler gives a pragma to the declaration after it however far
    /// down that is. A section line does drop them, because no declaration of this section
    /// follows; those lines stay unclaimed, so a change to them warns rather than vanishing.
    /// A declaration's span starts at its first attribute, so a changed attribute marks its row
    /// and a review comment on that line lands on it (2026-10-23).
    /// </para>
    /// <para>
    /// <b>A line is not a unit.</b> IEC 61131-3 has no line break in its grammar: a declaration
    /// ends at its semicolon wherever the next begins, and a section can open, declare and close
    /// on one line. Each line's code is taken in pieces (<see cref="Pieces"/>) — a section
    /// keyword, every statement that ends on the line, and whatever is left open for the next —
    /// since the 2026-10-25 review's item 9. Before that a line was one thing: one declaration,
    /// so <c>a : INT; b : BOOL;</c> tabled a variable <c>a</c> of type <c>INT; b : BOOL</c>; or
    /// one keyword line, so <c>VAR a : INT; END_VAR</c> opened a section, drew nothing and claimed
    /// the line as framing. Neither was reported as not shown, which is the part that matters: a
    /// table that silently reads a declaration wrong is read as the truth before a commit.
    /// </para>
    /// </remarks>
    private static IEnumerable<Declaration> Declarations(List<Line> lines, int from, Func<string, bool> isStructural)
    {
        var pending = new List<string>();
        var pendingLines = new List<int>();
        var attributes = new List<MemberAttribute>();
        var attributeLines = new List<int>();
        var buffer = new StringBuilder();
        var bufferComment = "";
        var bufferFirst = 0;
        var bufferLast = 0;

        Declaration Flush(string declaration)
        {
            var comment = bufferComment.Length > 0 ? bufferComment : string.Join(' ', pending);
            // What the row accounts for: its attribute lines, its comment lines and the declaration
            // itself — never the range between, which a pragma the table does not draw ({warning
            // …}, {IF …}) can sit in. The first version claimed the whole range from the first
            // attribute, so such a line was "shown" by a row that drew nothing of it and a change
            // there was reported complete (review, 2026-10-24).
            var used = attributeLines
                .Concat(Enumerable.Range(bufferFirst, bufferLast - bufferFirst + 1))
                .Concat(bufferComment.Length > 0 ? [] : pendingLines)
                .Distinct()
                .ToList();
            // The span starts at the first line the row accounts for, its comment lines above
            // included (2026-10-23): a comment the table draws in the row's cell is the row's,
            // so an edit to it is marked on the row and vouched for by it, and a review comment
            // on that line lands on the row rather than on nothing.
            var result = new Declaration(declaration, comment, null, used.Min(), bufferLast, [.. attributes], used);
            buffer.Clear();
            bufferComment = "";
            attributes.Clear();
            attributeLines.Clear();
            return result;
        }

        for (var i = Math.Max(0, from); i < lines.Count; i++)
        {
            var line = lines[i];
            if (buffer.Length == 0 && line.Attributes.Count > 0)
            {
                attributes.AddRange(line.Attributes.Select(a => a with { Lines = new LineSpan(i, i) }));
                attributeLines.Add(i);
            }

            if (buffer.Length == 0 && line.Code.Length == 0)
            {
                if (line.Comment.Length > 0)
                {
                    pending.Add(line.Comment);
                    pendingLines.Add(i);
                }
                else if (!line.Pragma)
                {
                    pending.Clear();
                    pendingLines.Clear();
                }

                continue;
            }

            var pieces = Pieces(line.Code, isStructural, buffer.Length == 0 ? "" : buffer + " ");
            // The keyword claims its line only when it is the whole line: shared with a
            // declaration, the line is the row's to claim (ClaimKeywordLines).
            var keywordsOnly = pieces.All(p => p.Structural);
            var continuing = buffer.Length > 0;
            var ended = false;
            foreach (var piece in pieces)
            {
                // A structural piece also ends a declaration still open: one missing its
                // semicolon must not swallow END_VAR and the next section's variables into a
                // single row.
                if (piece.Structural)
                {
                    if (buffer.Length > 0)
                    {
                        yield return Flush(buffer.ToString().Trim().TrimEnd(';').Trim());
                    }

                    pending.Clear();
                    pendingLines.Clear();
                    attributes.Clear();
                    attributeLines.Clear();
                    yield return new Declaration(null, "", piece.Text, i, i, [], keywordsOnly ? [i] : []);
                    continue;
                }

                if (buffer.Length == 0)
                {
                    bufferFirst = i;
                }
                else if (continuing && line.Attributes.Count > 0)
                {
                    // Written inside a declaration that spans lines: still this one's.
                    attributes.AddRange(line.Attributes.Select(a => a with { Lines = new LineSpan(i, i) }));
                    continuing = false;
                }

                bufferLast = i;
                buffer.Append(buffer.Length == 0 ? "" : " ").Append(piece.Text);
                // The line's comment is every declaration's that ends on it: it is written
                // beside them all, and an edit to it must mark each row it stands beside.
                if (line.Comment.Length > 0)
                {
                    bufferComment = line.Comment;
                }

                if (!piece.Complete)
                {
                    continue;
                }

                yield return Flush(buffer.ToString().TrimEnd().TrimEnd(';').Trim());
                pending.Clear();
                pendingLines.Clear();
                ended = true;
            }

            if (ended)
            {
                Trail(i);
            }
        }

        // Written after this line's code, so they wait for the declaration that follows.
        void Trail(int i)
        {
            if (lines[i].Trailing.Count > 0)
            {
                attributes.AddRange(lines[i].Trailing.Select(a => a with { Lines = new LineSpan(i, i) }));
                attributeLines.Add(i);
            }
        }
    }

    /// <summary>One declaration, or one section keyword in its place, as <see cref="Declarations"/> hands them back.</summary>
    /// <param name="First">Its first line, from its first attribute when it has one; zero-based.</param>
    /// <param name="Claimed">
    /// Every line it accounts for: its own, its attributes', and the comment lines above it that
    /// became its comment. For a section keyword, its line when the keyword is all the line
    /// holds, and nothing when a declaration shares the line.
    /// </param>
    private sealed record Declaration(
        string? Text, string Comment, string? Structural, int First, int Last,
        IReadOnlyList<MemberAttribute> Attributes, IReadOnlyList<int> Claimed);

    /// <summary>One piece of a line's code: a section keyword, or a statement, complete at its semicolon or left open for the next line.</summary>
    private sealed record Piece(string Text, bool Structural, bool Complete);

    /// <summary>
    /// A line's code in the pieces <see cref="Declarations"/> reads: each section keyword (with
    /// its CONSTANT / RETAIN / PERSISTENT qualifiers, or a namespace's name) on its own, each
    /// statement up to its semicolon on its own, and the tail with no semicolon left open.
    /// </summary>
    /// <param name="open">
    /// The declaration still open from the lines above, for the depth a semicolon is judged at:
    /// a bracket opened on the line above is still open here, and <c>3];</c> ends the statement
    /// it closes, not none.
    /// </param>
    /// <remarks>
    /// A keyword is cut from what follows only when that holds a declaration (a top-level colon),
    /// ends a statement (a top-level semicolon) or is another keyword. A keyword line with some
    /// other word after it — a qualifier this reader does not know — stays one keyword, as it
    /// always was, rather than becoming a keyword and an open statement that the next line's
    /// declaration would be glued onto.
    /// </remarks>
    private static List<Piece> Pieces(string code, Func<string, bool> isStructural, string open)
    {
        var pieces = new List<Piece>();
        while (code.Length > 0)
        {
            if (isStructural(code))
            {
                var (keyword, rest) = SplitKeyword(code);
                if (rest.Length > 0
                    && (TopLevel(rest, ':', skipAssign: true) >= 0 || TopLevel(rest, ';', skipAssign: false) >= 0 || isStructural(rest)))
                {
                    pieces.Add(new Piece(keyword, true, true));
                    code = rest;
                    open = "";
                    continue;
                }

                pieces.Add(new Piece(code, true, true));
                break;
            }

            var end = TopLevel(open + code, ';', skipAssign: false) - open.Length;
            if (end < 0)
            {
                pieces.Add(new Piece(code, false, false));
                break;
            }

            var statement = code[..end].Trim();
            if (statement.Length > 0)
            {
                pieces.Add(new Piece(statement, false, true));
            }

            code = code[(end + 1)..].Trim();
            open = "";
        }

        return pieces;
    }

    private static readonly string[] SectionQualifiers = ["CONSTANT", "RETAIN", "NON_RETAIN", "PERSISTENT"];

    /// <summary>The section keyword with its qualifiers, and the rest of the line's code after it.</summary>
    private static (string Keyword, string After) SplitKeyword(string code)
    {
        var words = Words(code);
        var take = words[0].Equals("NAMESPACE", StringComparison.OrdinalIgnoreCase) && words.Count > 1 ? 2 : 1;
        while (take < words.Count && SectionQualifiers.Contains(words[take], StringComparer.OrdinalIgnoreCase))
        {
            take++;
        }

        // By position in the original, so the rest keeps its own spacing.
        var at = 0;
        for (var w = 0; w < take; w++)
        {
            while (at < code.Length && code[at] is ' ' or '\t')
            {
                at++;
            }

            while (at < code.Length && code[at] is not (' ' or '\t'))
            {
                at++;
            }
        }

        return (code[..at].Trim(), code[at..].Trim());
    }

    /// <summary>One declaration's rows: <c>a, b : INT := 0</c> is two rows, with the same attributes.</summary>
    private static IEnumerable<MemberRow> Rows(Declaration d, string section) =>
        Rows(d.Text!, d.Comment, section, new LineSpan(d.First, d.Last))
            .Select(r => d.Attributes.Count == 0 ? r : r with { Attributes = d.Attributes });

    private static IEnumerable<MemberRow> Rows(string declaration, string comment, string section, LineSpan lines)
    {
        var colon = TopLevel(declaration, ':', skipAssign: true);
        if (colon < 0)
        {
            yield break;
        }

        var names = declaration[..colon];
        var rest = declaration[(colon + 1)..].Trim();
        var assign = TopLevelAssign(rest);
        var type = (assign >= 0 ? rest[..assign] : rest).Trim();
        var value = assign >= 0 ? rest[(assign + 2)..].Trim() : "";

        foreach (var raw in names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // "x AT %IX0.0": the address is part of what the IDE shows for the variable, so it
            // stays beside the name rather than being dropped.
            var at = raw.IndexOf(" AT ", StringComparison.OrdinalIgnoreCase);
            var name = at >= 0 ? $"{raw[..at].Trim()} ({raw[(at + 1)..].Trim()})" : raw;
            yield return Row(name, type, value, comment, section, lines);
        }
    }

    private static MemberRow Row(string name, string type, string value, string comment, string section, LineSpan lines) => new()
    {
        Lines = lines,
        Path = name,
        Name = name,
        Datatype = type,
        TypeReference = TypeReferenceIn(type),
        Section = section,
        StartValue = value,
        Comment = comment,
    };

    private static void AddHeading(List<MemberRow> rows, string section) =>
        rows.Add(new MemberRow { Path = section, Name = section, Section = section, IsSectionHeading = true });

    /// <summary>
    /// The user type a declaration names, for the host to link: the type with any
    /// <c>ARRAY[..] OF</c>, <c>POINTER TO</c> or <c>REFERENCE TO</c> taken off, unless it is
    /// elementary. A library type the host cannot find simply gets no link.
    /// </summary>
    private static string TypeReferenceIn(string type)
    {
        var t = type.Trim();
        while (true)
        {
            var of = t.IndexOf(" OF ", StringComparison.OrdinalIgnoreCase);
            if (t.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase) && of >= 0)
            {
                t = t[(of + 4)..].Trim();
                continue;
            }

            if (t.StartsWith("POINTER TO ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("REFERENCE TO ", StringComparison.OrdinalIgnoreCase))
            {
                t = t[(t.IndexOf(" TO ", StringComparison.OrdinalIgnoreCase) + 4)..].Trim();
                continue;
            }

            break;
        }

        var bare = t.Split('(', '[')[0].Trim();
        return bare.Length == 0 || Elementary.Contains(bare) || !IsIdentifier(bare) ? "" : bare;
    }

    private static bool IsIdentifier(string text) =>
        text.Split('.').All(part => part.Length > 0 && (char.IsLetter(part[0]) || part[0] == '_')
                                    && part.All(c => char.IsLetterOrDigit(c) || c == '_'));

    /// <summary>
    /// The heading for a <c>VAR…</c> line, or null when the line opens no section. A plain
    /// <c>VAR</c> keeps its value between calls in a program or function block and does not in a
    /// function or method, which is TIA's Static/Temp distinction.
    /// </summary>
    private static string? SectionOf(string line, Kind kind)
    {
        var words = Words(line);
        if (words.Count == 0)
        {
            return null;
        }

        var name = words[0].ToUpperInvariant() switch
        {
            "VAR_INPUT" => "Input",
            "VAR_OUTPUT" => "Output",
            "VAR_IN_OUT" => "InOut",
            "VAR_TEMP" => "Temp",
            "VAR_STAT" => "Static",
            "VAR_INST" => "Instance",
            "VAR_EXTERNAL" => "External",
            "VAR" => kind == Kind.Function ? "Temp" : "Static",
            _ => null,
        };

        if (name is null)
        {
            return null;
        }

        // VAR CONSTANT is its own section in TIA's words; any other qualifier is said after.
        return name is "Static" or "Temp" && words.Skip(1).Any(w => w.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase))
            ? "Constant"
            : Qualified(name, words.Skip(1));
    }

    private static string Qualified(string name, IEnumerable<string> qualifiers)
    {
        var said = qualifiers.Select(q => q.ToLowerInvariant()).Where(q => q is "constant" or "retain" or "persistent").ToList();
        return said.Count == 0 ? name : $"{name} ({string.Join(", ", said)})";
    }

    private static bool IsEnd(string line, string keyword) =>
        Words(line).FirstOrDefault()?.TrimEnd(';').Equals(keyword, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The header's type, stopping before EXTENDS / IMPLEMENTS.</summary>
    private static string UntilKeyword(string text)
    {
        foreach (var keyword in new[] { " EXTENDS ", " IMPLEMENTS " })
        {
            var at = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                text = text[..at];
            }
        }

        return text.Trim().TrimEnd(';').Trim();
    }

    private static List<string> Words(string line) =>
        [.. line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];

    private static int TopLevelAssign(string text)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length - 1; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                quote = ch == quote ? '\0' : quote;
                continue;
            }

            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ':' when depth == 0 && text[i + 1] == '=':
                    return i;
            }
        }

        return -1;
    }

    private static int TopLevel(string text, char target, bool skipAssign)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                quote = ch == quote ? '\0' : quote;
                continue;
            }

            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    continue;
                case '(' or '[':
                    depth++;
                    continue;
                case ')' or ']':
                    depth--;
                    continue;
            }

            if (ch == target && depth == 0 && !(skipAssign && i + 1 < text.Length && text[i + 1] == '='))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>One source line with its comment taken out of its code.</summary>
    /// <param name="Pragma">The line held only a pragma (<c>{attribute …}</c>), which keeps a comment above it attached.</param>
    /// <param name="Attributes">The <c>{attribute 'name' := 'value'}</c> pragmas on the line. Any other pragma is not one, and is left to <paramref name="Text"/>.</param>
    /// <param name="Text">The whole line as written, spacing collapsed: what an unclaimed line is compared by.</param>
    /// <param name="Open">The line ends inside a block comment, whose text lands on the line that closes it.</param>
    /// <param name="Trailing">Attributes written after code on the line (<c>x : INT; {attribute 'hide'}</c>): a pragma goes to the declaration after it, so these are the next one's.</param>
    private sealed record Line(
        string Code, string Comment, bool Pragma, IReadOnlyList<MemberAttribute> Attributes, string Text, bool Open,
        IReadOnlyList<MemberAttribute> Trailing);

    /// <summary>
    /// <c>attribute 'name'</c> or <c>attribute 'name' := 'value'</c>, the inside of an attribute
    /// pragma as IEC 61131-3 vendors write it. Anything else between braces (a conditional
    /// <c>{IF defined(...)}</c>, a <c>{warning}</c>, a TwinCAT <c>{region}</c>) is not an
    /// attribute and is not read as one.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*attribute\s+'([^']*)'\s*(?::=\s*'([^']*)'\s*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AttributePragma();

    private static string Collapse(string text) =>
        string.Join(' ', text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Every line split into code and comment, with block comments (which may span lines) and
    /// pragmas removed from the code and quoted strings left alone.
    /// </summary>
    private static List<Line> Clean(string text)
    {
        var result = new List<Line>();

        // A depth, not a flag. IEC 61131-3 permits nested (* … (* … *) … *) and CODESYS
        // enables nested comments by default, so a bool ended the comment at the FIRST
        // "*)" — everything after it up to the real closer was tabled as members that do
        // not exist, while the declaration's real tail was swallowed. The table is what a
        // reviewer reads before committing, and a table quietly inventing rows is the
        // kind of wrong that gets acted on.
        var block = 0;
        var blockText = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var code = new StringBuilder();
            var comment = new StringBuilder();
            var pragma = false;
            var attributes = new List<MemberAttribute>();
            var trailing = new List<MemberAttribute>();
            var quote = '\0';
            for (var i = 0; i < raw.Length; i++)
            {
                var ch = raw[i];
                if (block > 0)
                {
                    if (ch == '(' && i + 1 < raw.Length && raw[i + 1] == '*')
                    {
                        block++;
                        blockText.Append("(*");
                        i++;
                    }
                    else if (ch == '*' && i + 1 < raw.Length && raw[i + 1] == ')')
                    {
                        block--;
                        if (block == 0)
                        {
                            comment.Append(blockText.ToString().Trim());
                            blockText.Clear();
                        }
                        else
                        {
                            blockText.Append("*)");
                        }

                        i++;
                    }
                    else
                    {
                        blockText.Append(ch);
                    }

                    continue;
                }

                if (quote != '\0')
                {
                    code.Append(ch);
                    quote = ch == quote ? '\0' : quote;
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    quote = ch;
                    code.Append(ch);
                }
                else if (ch == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
                {
                    comment.Append(raw[(i + 2)..].Trim());
                    break;
                }
                else if (ch == '(' && i + 1 < raw.Length && raw[i + 1] == '*')
                {
                    block = 1;
                    i++;
                }
                else if (ch == '{')
                {
                    var close = raw.IndexOf('}', i);
                    pragma = true;
                    if (close > i && AttributePragma().Match(raw[(i + 1)..close]) is { Success: true } m)
                    {
                        (code.ToString().Trim().Length > 0 ? trailing : attributes)
                            .Add(new MemberAttribute { Name = m.Groups[1].Value, Value = m.Groups[2].Value });
                    }

                    i = close < 0 ? raw.Length : close;
                }
                else
                {
                    code.Append(ch);
                }
            }

            if (block > 0)
            {
                blockText.Append(' ');
            }

            var codeText = code.ToString().Trim();
            result.Add(new Line(codeText, comment.ToString().Trim(), pragma && codeText.Length == 0, attributes, Collapse(raw), block > 0, trailing));
        }

        return result;
    }
}
