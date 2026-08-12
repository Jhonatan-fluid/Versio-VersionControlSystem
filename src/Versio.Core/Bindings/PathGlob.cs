using System.Text.RegularExpressions;

namespace Versio.Core.Bindings;

/// <summary>
/// Matches repo-relative paths against the <c>ignore</c> globs in <c>repo.json</c>.
/// </summary>
/// <remarks>
/// Shared by <see cref="OrphanScanner"/>, which decides what to <i>offer</i>, and
/// <see cref="Adoption"/>, which decides what to <i>allow</i>. Those two must agree: a
/// project hidden from the list but adoptable through some other route would produce a
/// binding the user was never shown.
/// </remarks>
internal static class PathGlob
{
    public static bool Matches(string path, IReadOnlyList<string> globs) =>
        globs.Any(g => ToRegex(g).IsMatch(path));

    private static Regex ToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
