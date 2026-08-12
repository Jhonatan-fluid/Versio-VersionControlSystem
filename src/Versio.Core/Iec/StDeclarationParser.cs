using System.Text.RegularExpressions;

namespace Versio.Core.Iec;

public enum PouKind
{
    Unknown = 0,
    Program,
    FunctionBlock,
    Function,
    Interface,
    Dut,
}

public enum DutKind
{
    None = 0,
    Structure,
    Enumeration,
    Alias,
    Union,
}

/// <summary>Everything needed to call <c>create_pou</c> / <c>create_dut</c> / <c>create_interface</c>.</summary>
public sealed record DeclarationInfo
{
    public PouKind Kind { get; init; } = PouKind.Unknown;
    public DutKind Dut { get; init; } = DutKind.None;
    public string? Name { get; init; }

    /// <summary>Return type of a FUNCTION, or the base type of an ALIAS DUT.</summary>
    public string? ReturnOrBaseType { get; init; }

    /// <summary>The type named by EXTENDS.</summary>
    public string? Extends { get; init; }

    /// <summary>Types named by IMPLEMENTS.</summary>
    public IReadOnlyList<string> Implements { get; init; } = [];

    /// <summary>True when we understood enough to create the object.</summary>
    public bool CanCreate => Kind != PouKind.Unknown && !string.IsNullOrEmpty(Name);
}

/// <summary>
/// Reads the header of an ST declaration to work out what kind of object it is.
/// </summary>
/// <remarks>
/// <para>
/// Needed because creating a missing object requires knowing which factory to call and
/// with what arguments — and all of it is already stated in the declaration text. Without
/// this, pulling a teammate's brand-new POU can only warn.
/// </para>
/// <para>
/// Deliberately a header parser, not an ST parser: it reads far enough to identify the
/// object and stops. Anything it does not confidently recognise comes back as
/// <see cref="PouKind.Unknown"/> so the importer reports rather than guesses — creating the
/// wrong kind of object is worse than creating none.
/// </para>
/// </remarks>
public static partial class StDeclarationParser
{
    [GeneratedRegex(@"\(\*.*?\*\)", RegexOptions.Singleline)] private static partial Regex BlockComment();
    [GeneratedRegex(@"//[^\n]*")] private static partial Regex LineComment();
    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.Singleline)] private static partial Regex Attribute();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    /// <summary>Access and inheritance modifiers that may sit between the keyword and the
    /// name. They carry no information we need.</summary>
    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABSTRACT", "FINAL", "PUBLIC", "PRIVATE", "PROTECTED", "INTERNAL",
    };

    public static DeclarationInfo Parse(string? declaration)
    {
        var text = Strip(declaration);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DeclarationInfo();
        }

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            return new DeclarationInfo();
        }

        return tokens[0].ToUpperInvariant() switch
        {
            "PROGRAM" => ParsePou(tokens, PouKind.Program),
            "FUNCTION_BLOCK" => ParsePou(tokens, PouKind.FunctionBlock),
            "FUNCTION" => ParsePou(tokens, PouKind.Function),
            "INTERFACE" => ParsePou(tokens, PouKind.Interface),
            "TYPE" => ParseDut(tokens, text),
            _ => new DeclarationInfo(),
        };
    }

    private static DeclarationInfo ParsePou(List<string> tokens, PouKind kind)
    {
        var i = 1;
        while (i < tokens.Count && Modifiers.Contains(tokens[i]))
        {
            i++;
        }

        if (i >= tokens.Count)
        {
            return new DeclarationInfo { Kind = kind };
        }

        var name = tokens[i++];
        string? returnType = null;
        string? extends = null;
        var implements = new List<string>();

        // A FUNCTION declares its return type after a colon: FUNCTION Add : INT
        if (kind == PouKind.Function && i < tokens.Count && tokens[i] == ":")
        {
            i++;
            if (i < tokens.Count)
            {
                returnType = tokens[i++];
            }
        }

        while (i < tokens.Count)
        {
            var token = tokens[i].ToUpperInvariant();
            if (token == "EXTENDS")
            {
                i++;
                if (i < tokens.Count)
                {
                    extends = tokens[i++];
                }

                // INTERFACE may extend several interfaces.
                while (i < tokens.Count && tokens[i] == ",")
                {
                    i++;
                    if (i < tokens.Count)
                    {
                        implements.Add(tokens[i++]);
                    }
                }
            }
            else if (token == "IMPLEMENTS")
            {
                i++;
                while (i < tokens.Count)
                {
                    implements.Add(tokens[i++]);
                    if (i < tokens.Count && tokens[i] == ",")
                    {
                        i++;
                        continue;
                    }

                    break;
                }
            }
            else if (token is "VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT"
                     or "VAR_GLOBAL" or "VAR_TEMP" or "VAR_STAT" or "VAR_INST")
            {
                break;   // past the header
            }
            else
            {
                i++;
            }
        }

        return new DeclarationInfo
        {
            Kind = kind,
            Name = name,
            ReturnOrBaseType = returnType,
            Extends = extends,
            Implements = implements,
        };
    }

    private static DeclarationInfo ParseDut(List<string> tokens, string text)
    {
        // TYPE <name> : <body>
        if (tokens.Count < 2)
        {
            return new DeclarationInfo { Kind = PouKind.Dut };
        }

        var name = tokens[1];
        var upper = text.ToUpperInvariant();

        // Order matters: a UNION and a STRUCT both contain member lists, and an enum's
        // parenthesised body can appear inside a struct member's initialiser.
        if (Contains(upper, "UNION"))
        {
            return Dut(name, DutKind.Union, null);
        }

        if (Contains(upper, "STRUCT"))
        {
            return Dut(name, DutKind.Structure, null);
        }

        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            var body = text[(colon + 1)..].TrimStart();
            if (body.StartsWith('('))
            {
                return Dut(name, DutKind.Enumeration, null);
            }

            // TYPE MyAlias : INT;  -> alias over a base type
            var baseType = body.TrimEnd();
            var end = baseType.IndexOf(';');
            if (end >= 0)
            {
                baseType = baseType[..end];
            }

            baseType = baseType.Replace("END_TYPE", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(baseType))
            {
                return Dut(name, DutKind.Alias, baseType);
            }
        }

        return Dut(name, DutKind.Structure, null);
    }

    private static bool Contains(string haystack, string word) =>
        Regex.IsMatch(haystack, $@"\b{Regex.Escape(word)}\b");

    private static DeclarationInfo Dut(string name, DutKind kind, string? baseType) => new()
    {
        Kind = PouKind.Dut,
        Dut = kind,
        Name = name,
        ReturnOrBaseType = baseType,
    };

    /// <summary>Remove comments and attribute pragmas, which can otherwise sit before the
    /// keyword and hide it.</summary>
    private static string Strip(string? declaration)
    {
        if (string.IsNullOrEmpty(declaration))
        {
            return string.Empty;
        }

        var text = declaration.Replace("\r\n", "\n");
        text = BlockComment().Replace(text, " ");
        text = LineComment().Replace(text, " ");
        text = Attribute().Replace(text, " ");
        return text.Trim();
    }

    private static List<string> Tokenize(string text)
    {
        var normalized = Whitespace().Replace(text, " ");
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var c in normalized)
        {
            if (c is ':' or ',' or ';' or '(' or ')')
            {
                Flush(tokens, current);
                if (c is ':' or ',')
                {
                    tokens.Add(c.ToString());
                }

                if (c is ';' or '(')
                {
                    return tokens;   // header ends here
                }
            }
            else if (c == ' ')
            {
                Flush(tokens, current);
            }
            else
            {
                current.Append(c);
            }
        }

        Flush(tokens, current);
        return tokens;
    }

    private static void Flush(List<string> tokens, System.Text.StringBuilder sb)
    {
        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
            sb.Clear();
        }
    }
}
