using Versio.Core.Diff;

namespace Versio.Core.Projects;

/// <summary>
/// Links between structures by name (docs/web/FINISH.md, C2 and Step 3c): what an identifier in
/// one object's source opens, and which objects' sources name a given object. Both answered from
/// <see cref="ObjectIndex"/>, so both hosts get the same answer for the same click.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same project only, and never itself.</b> A name means the object of that name in the
/// project the source belongs to, as a member's type does (<c>LinkTypes</c> on both hosts): two
/// projects may each have an <c>FB_Motor</c>, and a POU naming itself (a recursive call, its own
/// header) is not a link anywhere. A library or system name finds no exported object and stays
/// text — a link to nowhere is worse than none.
/// </para>
/// <para>
/// <b>Only words the tokenizer left plain.</b> Keywords and built-in types are classed before
/// this is asked; comments and strings are never asked. So a remark that mentions <c>FB_Pump</c>
/// stays a remark, and <c>BOOL</c> stays a type.
/// </para>
/// </remarks>
public static class ObjectLinks
{
    /// <summary>
    /// The resolver for identifiers in <paramref name="sourceFile"/>: a name to what the host
    /// opens for the object of that name in the same project, through <paramref name="target"/>
    /// (a repository path on the web, a guid on the desktop); null when the file is no exported
    /// object's, in which case nothing is linked.
    /// </summary>
    public static Func<string, string?>? Resolver(ObjectIndex index, string sourceFile, Func<ObjectFiles, string?> target)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(target);
        if (index.Lookup(sourceFile) is not { } owner)
        {
            return null;
        }

        return name =>
            string.Equals(name, owner.Name, StringComparison.OrdinalIgnoreCase)
                ? null
                : index.FindByName(owner.Project, name) is { } files && files.Guid != owner.Guid ? target(files) : null;
    }

    /// <summary>
    /// The names <paramref name="text"/> refers to, among <paramref name="candidates"/>: every
    /// identifier the tokenizer leaves plain, outside comments and strings, compared without case
    /// as IEC names are. The "used by" index is this, asked of every object's source in a project.
    /// </summary>
    public static IReadOnlySet<string> NamesReferenced(string text, SyntaxLanguage language, IReadOnlySet<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(candidates);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text) || candidates.Count == 0)
        {
            return found;
        }

        foreach (var line in text.Split('\n'))
        {
            foreach (var token in SyntaxTokenizer.Tokenize(line, language))
            {
                if (token.Kind != TokenKind.Plain || token.Length == 0 || !(char.IsLetter(line[token.Start]) || line[token.Start] == '_'))
                {
                    continue;
                }

                var word = line.Substring(token.Start, token.Length);
                if (candidates.Contains(word))
                {
                    found.Add(word);
                }
            }
        }

        return found;
    }
}
