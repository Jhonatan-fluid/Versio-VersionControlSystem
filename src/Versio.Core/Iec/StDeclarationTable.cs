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
public static class StDeclarationTable
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
            Members = table.Members.Select(r => r with { Lines = r.Lines?.Shift(firstLine) }).ToList(),
        };
    }

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

        var words = Words(lines[header].Code);
        var first = words[0].ToUpperInvariant();
        return first switch
        {
            "PROGRAM" or "FUNCTION_BLOCK" => Block(lines, header, words, Kind.Block),
            "FUNCTION" or "METHOD" => Block(lines, header, words, Kind.Function),
            _ when first.StartsWith("PROPERTY", StringComparison.Ordinal) => Block(lines, header, words, Kind.Function),
            "TYPE" => Type(lines, header, words),
            "VAR_GLOBAL" or "NAMESPACE" => Variables(lines, header, words, fallbackName),
            _ => null,
        };
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
    private static MemberTable? Block(List<Line> lines, int header, List<string> words, Kind kind)
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
        if (kind == Kind.Function && colon >= 0)
        {
            var type = UntilKeyword(lines[header].Code[(colon + 1)..].Trim());
            if (type.Length > 0)
            {
                AddHeading(rows, "Return");
                rows.Add(Row(name, type, "", lines[header].Comment, "Return", new LineSpan(header, header)));
            }
        }

        string? section = null;
        foreach (var (decl, comment, structural, from, to) in Declarations(
                     lines, header + 1, line => SectionOf(line, kind) is not null || IsEnd(line, "END_VAR")))
        {
            if (structural is not null)
            {
                // Closed by END_VAR: anything before the next VAR is not a variable.
                section = SectionOf(structural, kind);
                continue;
            }

            if (section is null)
            {
                continue;
            }

            if (!rows.Any(r => r.IsSectionHeading && r.Section == section))
            {
                AddHeading(rows, section);
            }

            rows.AddRange(Rows(decl!, comment, section, new LineSpan(from, to)));
        }

        return new MemberTable { Name = name, Members = rows, IsInterface = true };
    }

    /// <summary>A structure, union or enumeration: one row per member, no headings.</summary>
    private static MemberTable? Type(List<Line> lines, int header, List<string> words)
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
            return Enumeration(lines, header, name, afterColon);
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
        var rows = Declarations(lines, start + 1, line => IsEnd(line, "END_" + opener))
            .TakeWhile(d => d.Structural is null)
            .SelectMany(d => Rows(d.Declaration!, d.Comment, "", new LineSpan(d.First, d.Last)))
            .ToList();
        return new MemberTable { Name = name, Members = rows, IsDataType = true };
    }

    /// <summary>
    /// An enumeration's values: the name, the value it is given (as the default-value column), and
    /// the base type after the closing parenthesis as each row's type, as the IDE shows them.
    /// </summary>
    private static MemberTable Enumeration(List<Line> lines, int header, string name, string afterColon)
    {
        var close = afterColon.LastIndexOf(')');
        var baseType = close >= 0
            ? afterColon[(close + 1)..].Replace("END_TYPE", "", StringComparison.OrdinalIgnoreCase).Trim().Trim(';').Trim()
            : "";

        var rows = new List<MemberRow>();
        var depth = 0;
        var pending = new List<string>();
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

            if (line.Code.Contains(')', StringComparison.Ordinal) && depth == 0 && i > header)
            {
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
    private static MemberTable? Variables(List<Line> lines, int header, List<string> words, string fallbackName)
    {
        var name = words[0].Equals("NAMESPACE", StringComparison.OrdinalIgnoreCase) && words.Count > 1
            ? words[1]
            : fallbackName;

        var found = new List<(string Section, string Declaration, string Comment, LineSpan Lines)>();
        static string First(string line) => Words(line).FirstOrDefault()?.TrimEnd(';').ToUpperInvariant() ?? "";

        string? section = null;
        foreach (var (decl, comment, structural, from, to) in Declarations(
                     lines, header, line => First(line) is "VAR_GLOBAL" or "END_VAR" or "NAMESPACE" or "END_NAMESPACE"))
        {
            if (structural is not null)
            {
                section = First(structural) == "VAR_GLOBAL" ? Qualified("Global", Words(structural).Skip(1)) : null;
                continue;
            }

            if (section is not null)
            {
                found.Add((section, decl!, comment, new LineSpan(from, to)));
            }
        }

        var headed = found.Select(f => f.Section).Distinct().Count() > 1;
        var rows = new List<MemberRow>();
        foreach (var (s, decl, comment, span) in found)
        {
            if (headed && !rows.Any(r => r.IsSectionHeading && r.Section == s))
            {
                AddHeading(rows, s);
            }

            rows.AddRange(Rows(decl, comment, s, span));
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
    /// </remarks>
    private static IEnumerable<(string? Declaration, string Comment, string? Structural, int First, int Last)> Declarations(
        List<Line> lines, int from, Func<string, bool> isStructural)
    {
        var pending = new List<string>();
        var buffer = new StringBuilder();
        var bufferComment = "";
        var bufferFirst = 0;
        var bufferLast = 0;
        for (var i = Math.Max(0, from); i < lines.Count; i++)
        {
            var line = lines[i];
            if (buffer.Length == 0 && line.Code.Length == 0)
            {
                if (line.Comment.Length > 0)
                {
                    pending.Add(line.Comment);
                }
                else if (!line.Pragma)
                {
                    pending.Clear();
                }

                continue;
            }

            // A structural line also ends a declaration still open: one missing its semicolon
            // must not swallow END_VAR and the next section's variables into a single row.
            if (line.Code.Length > 0 && isStructural(line.Code))
            {
                if (buffer.Length > 0)
                {
                    yield return (buffer.ToString().Trim().TrimEnd(';').Trim(),
                        bufferComment.Length > 0 ? bufferComment : string.Join(' ', pending), null, bufferFirst, bufferLast);
                    buffer.Clear();
                    bufferComment = "";
                }

                pending.Clear();
                yield return (null, "", line.Code, i, i);
                continue;
            }

            if (buffer.Length == 0)
            {
                bufferFirst = i;
            }

            bufferLast = i;
            buffer.Append(buffer.Length == 0 ? "" : " ").Append(line.Code);
            if (line.Comment.Length > 0)
            {
                bufferComment = line.Comment;
            }

            if (!EndsStatement(buffer.ToString()))
            {
                continue;
            }

            yield return (buffer.ToString().TrimEnd().TrimEnd(';').Trim(),
                bufferComment.Length > 0 ? bufferComment : string.Join(' ', pending), null, bufferFirst, bufferLast);
            buffer.Clear();
            bufferComment = "";
            pending.Clear();
        }
    }

    /// <summary>One declaration's rows: <c>a, b : INT := 0</c> is two rows.</summary>
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

    /// <summary>A statement is finished at a semicolon outside parentheses, brackets and strings.</summary>
    private static bool EndsStatement(string text) => TopLevel(text, ';', skipAssign: false) >= 0;

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
    private sealed record Line(string Code, string Comment, bool Pragma);

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
            result.Add(new Line(codeText, comment.ToString().Trim(), pragma && codeText.Length == 0));
        }

        return result;
    }
}
