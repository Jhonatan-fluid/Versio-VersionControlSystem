namespace Versio.Core.Git;

/// <summary>
/// Parses <c>git ls-files -z -c -o --exclude-standard</c>.
/// </summary>
/// <remarks>
/// The same <c>-z</c> contract as <see cref="PorcelainV2Parser"/>: without it git quotes and
/// escapes paths containing spaces or non-ASCII, and CODESYS object names routinely contain
/// both. NUL-separated output needs no unquoting.
/// </remarks>
public static class WorkingTreeListParser
{
    public static IReadOnlyList<string> Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in output.Split('\0'))
        {
            if (string.IsNullOrEmpty(field))
            {
                continue;
            }

            var path = field.Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            paths.Add(path);
        }

        return paths;
    }
}
