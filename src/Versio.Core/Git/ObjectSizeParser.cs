namespace Versio.Core.Git;

/// <summary>
/// Parses <c>git cat-file --batch-check=%(objectsize) %(objecttype)</c>, one answer per
/// object name that was written to its standard input, in the order they were written.
/// </summary>
/// <remarks>
/// <para>
/// The answers carry no name of their own, which is why the order is the contract: git
/// prints the size and type for an object it resolved, and <c>&lt;name&gt; missing</c> for
/// one it could not, so the caller pairs each line with the name it sent. Asking for the
/// name back through <c>%(objectname)</c> would give the hash rather than the
/// <c>:0:path</c> that was sent, and <c>%(rest)</c> splits the input at its first space,
/// which is the one thing a CODESYS object name cannot survive.
/// </para>
/// <para>
/// Only a <c>blob</c> has a size that means "bytes the commit records": a submodule's
/// gitlink resolves to a commit, whose object size says nothing about a file, so it is
/// answered as unknown rather than as a number.
/// </para>
/// </remarks>
public static class ObjectSizeParser
{
    /// <summary>One entry per answer line: the blob's size, or null for a name git could not resolve to a blob.</summary>
    public static IReadOnlyList<long?> Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var sizes = new List<long?>();

        foreach (var raw in output.Split('\n'))
        {
            // The runner appends '\n' after every line including the last, so the final
            // split field is empty and is not an answer.
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(' ');
            sizes.Add(parts.Length == 2 && parts[1] == "blob" && long.TryParse(parts[0], out var bytes)
                ? bytes
                : null);
        }

        return sizes;
    }
}
