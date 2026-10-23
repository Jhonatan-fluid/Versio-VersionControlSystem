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
            // Whitespace, not only empty: the CLI runner appends '\n' after the last NUL, and
            // a "\n" path drew as a nameless file at the Explorer's root (2026-10-23). No file
            // on Windows is named only whitespace, so nothing real is skipped.
            if (string.IsNullOrWhiteSpace(field))
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
