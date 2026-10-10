namespace Versio.Core.Diff;

/// <summary>What a highlighted span of source is, for colouring purposes.</summary>
public enum TokenKind
{
    Plain,
    Keyword,
    Type,
    Comment,
    String,
    Number,
    Directive,
    Punctuation,
}

/// <summary>A coloured run within one line, held as offsets rather than substrings so
/// highlighting a large file does not allocate a string per token.</summary>
public readonly record struct TokenSpan(int Start, int Length, TokenKind Kind);

/// <summary>
/// How one language is tokenized. Table-driven, so adding a language is configuration
/// rather than code.
/// </summary>
public sealed record SyntaxLanguage
{
    public required string Name { get; init; }
    public IReadOnlySet<string> Keywords { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Types { get; init; } = new HashSet<string>();

    /// <summary>Prefixes that comment out the rest of the line.</summary>
    public IReadOnlyList<string> LineComments { get; init; } = [];

    /// <summary>Block comment delimiters, e.g. <c>("(*", "*)")</c> for Structured Text.</summary>
    public IReadOnlyList<(string Open, string Close)> BlockComments { get; init; } = [];

    /// <summary>Characters that open and close a string literal.</summary>
    public IReadOnlyList<char> StringDelimiters { get; init; } = ['"'];

    /// <summary>Prefix marking a preprocessor or pragma line, e.g. <c>#</c> or <c>{</c>.</summary>
    public IReadOnlyList<string> DirectivePrefixes { get; init; } = [];

    public bool CaseSensitive { get; init; } = true;
    public bool HighlightNumbers { get; init; } = true;

    /// <summary>Nothing is highlighted. The honest answer for an unknown format — guessing
    /// wrong is worse than plain text.</summary>
    public static readonly SyntaxLanguage PlainText = new() { Name = "Plain text" };
}

/// <summary>
/// Maps a file to its language.
/// </summary>
/// <remarks>
/// Extension first, then a shebang for extensionless scripts. A CODESYS repository holds
/// far more than CODESYS — READMEs, CI YAML, build scripts, tooling — and rendering all of
/// it as undifferentiated grey would make Versio a worse tool than a plain git client.
/// </remarks>
public static class LanguageCatalog
{
    /// <summary>
    /// IEC 61131-3 Structured Text. Public so a vendor's format can name it for an extension
    /// this catalog does not map, without the catalog learning that vendor's extensions
    /// (<c>IPlcProjectFormat.SyntaxFor</c>).
    /// </summary>
    public static readonly SyntaxLanguage StructuredText = new()
    {
        Name = "Structured Text",
        // IEC 61131-3 is case-insensitive.
        CaseSensitive = false,
        LineComments = ["//"],
        BlockComments = [("(*", "*)")],
        StringDelimiters = ['\'', '"'],
        // {attribute 'qualified_only'} and similar pragmas open real declarations.
        DirectivePrefixes = ["{"],
        Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PROGRAM", "END_PROGRAM", "FUNCTION", "END_FUNCTION",
            "FUNCTION_BLOCK", "END_FUNCTION_BLOCK", "INTERFACE", "END_INTERFACE",
            "METHOD", "END_METHOD", "PROPERTY", "END_PROPERTY",
            "ACTION", "END_ACTION", "TYPE", "END_TYPE",
            "STRUCT", "END_STRUCT", "UNION", "END_UNION",
            "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL",
            "VAR_TEMP", "VAR_STAT", "VAR_INST", "VAR_CONFIG", "END_VAR",
            "CONSTANT", "RETAIN", "PERSISTENT", "AT", "EXTENDS", "IMPLEMENTS",
            "ABSTRACT", "FINAL", "PUBLIC", "PRIVATE", "PROTECTED", "INTERNAL",
            "IF", "THEN", "ELSIF", "ELSE", "END_IF",
            "CASE", "OF", "END_CASE",
            "FOR", "TO", "BY", "DO", "END_FOR",
            "WHILE", "END_WHILE", "REPEAT", "UNTIL", "END_REPEAT",
            "EXIT", "CONTINUE", "RETURN", "JMP",
            "AND", "OR", "XOR", "NOT", "MOD",
            "TRUE", "FALSE", "NULL", "THIS", "SUPER", "REF", "ADR", "SIZEOF",
        },
        Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BOOL", "BYTE", "WORD", "DWORD", "LWORD",
            "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT",
            "REAL", "LREAL", "STRING", "WSTRING", "TIME", "LTIME",
            "DATE", "TIME_OF_DAY", "TOD", "DATE_AND_TIME", "DT",
            "ARRAY", "POINTER", "REFERENCE", "ANY",
        },
    };

    private static readonly SyntaxLanguage CSharp = new()
    {
        Name = "C#",
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        DirectivePrefixes = ["#"],
        Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "async", "await", "base", "break", "case", "catch", "checked",
            "class", "const", "continue", "default", "delegate", "do", "else", "enum",
            "event", "explicit", "extern", "finally", "fixed", "for", "foreach", "get",
            "goto", "if", "implicit", "in", "init", "interface", "internal", "is", "lock",
            "namespace", "new", "operator", "out", "override", "params", "private",
            "protected", "public", "readonly", "record", "ref", "return", "sealed", "set",
            "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "try",
            "typeof", "unchecked", "unsafe", "using", "var", "virtual", "volatile", "when",
            "where", "while", "yield", "true", "false", "null", "nameof", "required",
        },
        Types = new HashSet<string>(StringComparer.Ordinal)
        {
            "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object",
            "sbyte", "short", "string", "uint", "ulong", "ushort", "void", "dynamic",
            "Task", "List", "Dictionary", "IEnumerable", "IReadOnlyList", "Exception",
        },
    };

    private static readonly SyntaxLanguage Python = new()
    {
        Name = "Python",
        LineComments = ["#"],
        StringDelimiters = ['"', '\''],
        Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "as", "assert", "async", "await", "break", "class", "continue", "def",
            "del", "elif", "else", "except", "finally", "for", "from", "global", "if",
            "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
            "return", "try", "while", "with", "yield", "True", "False", "None", "self",
        },
        Types = new HashSet<string>(StringComparer.Ordinal)
        {
            "int", "float", "str", "bool", "list", "dict", "set", "tuple", "bytes", "object",
        },
    };

    /// <summary>XML. Public for the same reason as <see cref="StructuredText"/>.</summary>
    public static readonly SyntaxLanguage Xml = new()
    {
        Name = "XML",
        BlockComments = [("<!--", "-->")],
        StringDelimiters = ['"', '\''],
        HighlightNumbers = false,
    };

    private static readonly SyntaxLanguage Json = new()
    {
        Name = "JSON",
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        Keywords = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" },
    };

    private static readonly SyntaxLanguage Yaml = new()
    {
        Name = "YAML",
        LineComments = ["#"],
        StringDelimiters = ['"', '\''],
        Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "null", "yes", "no", "on", "off",
        },
    };

    private static readonly SyntaxLanguage Markdown = new()
    {
        Name = "Markdown",
        StringDelimiters = ['`'],
        DirectivePrefixes = ["#", ">", "-", "*"],
        HighlightNumbers = false,
    };

    private static readonly SyntaxLanguage PowerShell = new()
    {
        Name = "PowerShell",
        CaseSensitive = false,
        LineComments = ["#"],
        BlockComments = [("<#", "#>")],
        StringDelimiters = ['"', '\''],
        Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam",
            "else", "elseif", "end", "exit", "filter", "finally", "for", "foreach", "from",
            "function", "if", "in", "param", "process", "return", "switch", "throw", "trap",
            "try", "until", "using", "while", "true", "false", "null",
        },
    };

    private static readonly SyntaxLanguage JavaScript = new()
    {
        Name = "JavaScript",
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        StringDelimiters = ['"', '\'', '`'],
        Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "async", "await", "break", "case", "catch", "class", "const", "continue",
            "debugger", "default", "delete", "do", "else", "export", "extends", "finally",
            "for", "function", "if", "import", "in", "instanceof", "let", "new", "of",
            "return", "static", "super", "switch", "this", "throw", "try", "typeof", "var",
            "void", "while", "yield", "true", "false", "null", "undefined",
            "interface", "type", "enum", "implements", "readonly", "declare",
        },
        Types = new HashSet<string>(StringComparer.Ordinal)
        {
            "string", "number", "boolean", "object", "any", "unknown", "never", "Promise",
        },
    };

    private static readonly SyntaxLanguage Sql = new()
    {
        Name = "SQL",
        CaseSensitive = false,
        LineComments = ["--"],
        BlockComments = [("/*", "*/")],
        StringDelimiters = ['\''],
        Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "select", "from", "where", "insert", "into", "values", "update", "set",
            "delete", "create", "table", "alter", "drop", "join", "left", "right", "inner",
            "outer", "on", "group", "by", "order", "having", "and", "or", "not", "null",
            "as", "distinct", "union", "index", "view", "primary", "key", "foreign",
        },
    };

    private static readonly Dictionary<string, SyntaxLanguage> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".st"] = StructuredText,
            // Textual declaration files that are Structured Text under another extension.
            // Mapping them here, with no vendor name, is what lets the diff highlighter
            // light up a second vendor's objects without Diff/ learning who wrote them.
            [".scl"] = StructuredText,
            [".s7dcl"] = StructuredText,
            [".udt"] = StructuredText,
            [".cs"] = CSharp,
            [".csx"] = CSharp,
            [".py"] = Python,
            [".pyw"] = Python,
            [".xml"] = Xml,
            [".xaml"] = Xml,
            [".html"] = Xml,
            [".htm"] = Xml,
            [".svg"] = Xml,
            [".csproj"] = Xml,
            [".props"] = Xml,
            [".targets"] = Xml,
            [".config"] = Xml,
            [".json"] = Json,
            [".jsonc"] = Json,
            [".yaml"] = Yaml,
            [".yml"] = Yaml,
            [".md"] = Markdown,
            [".markdown"] = Markdown,
            [".ps1"] = PowerShell,
            [".psm1"] = PowerShell,
            [".psd1"] = PowerShell,
            [".js"] = JavaScript,
            [".mjs"] = JavaScript,
            [".ts"] = JavaScript,
            [".tsx"] = JavaScript,
            [".jsx"] = JavaScript,
            [".sql"] = Sql,
        };

    /// <summary>Extensionless files whose name alone identifies them.</summary>
    private static readonly Dictionary<string, SyntaxLanguage> ByFileName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".gitattributes"] = Yaml,
            [".gitignore"] = Yaml,
            ["dockerfile"] = Yaml,
            ["makefile"] = Yaml,
        };

    public static SyntaxLanguage ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SyntaxLanguage.PlainText;
        }

        var name = Path.GetFileName(path);
        if (ByFileName.TryGetValue(name, out var byName))
        {
            return byName;
        }

        var extension = Path.GetExtension(path);
        return ByExtension.TryGetValue(extension, out var language)
            ? language
            : SyntaxLanguage.PlainText;
    }

    /// <summary>Fall back to a shebang for extensionless scripts.</summary>
    public static SyntaxLanguage ForContent(string? path, string? firstLine)
    {
        var byPath = ForPath(path);
        if (byPath != SyntaxLanguage.PlainText || string.IsNullOrWhiteSpace(firstLine))
        {
            return byPath;
        }

        if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            return byPath;
        }

        if (firstLine.Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            return Python;
        }

        return firstLine.Contains("pwsh", StringComparison.OrdinalIgnoreCase)
            ? PowerShell
            : byPath;
    }

    public static IReadOnlyCollection<string> SupportedExtensions => ByExtension.Keys;
}
