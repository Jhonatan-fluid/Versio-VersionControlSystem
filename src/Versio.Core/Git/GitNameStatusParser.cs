namespace Versio.Core.Git;

/// <summary>One file, as one commit changed it.</summary>
/// <remarks>
/// Deliberately not a <see cref="FileChange"/>. That type carries a staged and an unstaged
/// side because the working tree has both; a commit has neither — it happened, and there is
/// one thing that happened to each path.
/// </remarks>
public sealed record CommitFile
{
    /// <summary>The path as the commit left it. For a rename, the new name.</summary>
    public required string Path { get; init; }

    public required ChangeKind Kind { get; init; }

    /// <summary>Previous path for a rename or copy, otherwise null.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>Rename/copy similarity, 0-100.</summary>
    public int? SimilarityScore { get; init; }
}

/// <summary>
/// Parses <c>git show --name-status -z</c> — the file list behind one commit.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>-z</c>, not the readable form.</b> Without it git quotes any path it considers
/// unusual: a space is left alone but a non-ASCII byte comes back as
/// <c>"K\303\274hlschrank.st"</c>, octal escapes and surrounding quotes included. Unpicking
/// that is a second parser nobody asked for, and the fixture corpus in this repository has
/// both spaces and umlauts in its object names. Under <c>-z</c> git emits the raw path and
/// separates every field with a NUL, which cannot occur in one.
/// </para>
/// <para>
/// Pure, and separate from the client that runs git, for the same reason
/// <see cref="GitLogParser"/> is: the shapes are then tested against real output without a
/// repository.
/// </para>
/// </remarks>
public static class GitNameStatusParser
{
    /// <summary>Field separator <c>-z</c> writes between every token.</summary>
    public const char Unit = '\0';

    /// <summary>
    /// Read the records git wrote, skipping anything it did not finish.
    /// </summary>
    /// <remarks>
    /// A truncated final record — a status with no path — is dropped rather than thrown over,
    /// matching <see cref="GitLogParser"/>. This drives a list somebody glances at, and taking
    /// the window down because one record arrived short would be a far worse failure than a
    /// missing row.
    /// </remarks>
    public static IReadOnlyList<CommitFile> Parse(string? output)
    {
        var files = new List<CommitFile>();

        if (string.IsNullOrEmpty(output))
        {
            return files;
        }

        // git runs the records together with no trailing separator of its own, and the
        // client appends a newline as it reassembles the stream. Neither is a field.
        var fields = output.Trim('\n', '\r').Split(Unit);

        for (var i = 0; i < fields.Length; i++)
        {
            var status = fields[i];
            if (status.Length == 0)
            {
                continue;
            }

            var kind = KindOf(status[0]);

            // R and C are the only two-path statuses: git writes the score, then where the
            // content came from, then where it went. Reading one path for them would leave
            // the destination sitting in the stream to be parsed as the next status.
            var paired = kind is ChangeKind.Renamed or ChangeKind.Copied;
            var needed = paired ? 2 : 1;

            if (i + needed >= fields.Length)
            {
                break;
            }

            files.Add(new CommitFile
            {
                Path = fields[i + needed],
                Kind = kind,
                OriginalPath = paired ? fields[i + 1] : null,
                SimilarityScore = Score(status),
            });

            i += needed;
        }

        return files;
    }

    /// <summary>
    /// The renames in a <c>--name-status -z</c> listing, old path → new path.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="Parse"/> rather than on a scan of its own, because the scan the
    /// client used to run skipped a one-path record one field at a time and so read the
    /// <i>path</i> of a modified <c>README.txt</c> as a status token: it began with
    /// <c>R</c>, the two fields after it were taken as the pair, and the real rename behind
    /// it was lost — the binding reported broken and the new project path untracked. The
    /// 2026-10-24 review's item 4. Walking records by their own width is the only way to
    /// know which field is a status, and <see cref="Parse"/> already does that. A copy is
    /// not a rename: its source is still where it was, so nothing needs re-linking.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Renames(string? output)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Parse(output))
        {
            if (file.Kind == ChangeKind.Renamed && file.OriginalPath is { } original)
            {
                renames[original] = file.Path;
            }
        }

        return renames;
    }

    /// <summary>
    /// One status letter, as a <see cref="ChangeKind"/>.
    /// </summary>
    /// <remarks>
    /// <c>T</c> — a file that became a symlink, or stopped being one — is reported as a
    /// modification, because that is what it is from the point of view of someone reading the
    /// diff. <c>X</c> means git itself hit a bug and <c>B</c> means a broken pairing; both are
    /// vanishingly rare and both mean the file changed somehow, so they say "modified" rather
    /// than claim the file is untouched.
    /// </remarks>
    private static ChangeKind KindOf(char status) => status switch
    {
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'U' => ChangeKind.Conflicted,
        _ => ChangeKind.Modified,
    };

    /// <summary>The digits trailing an R or C status, when git wrote any.</summary>
    private static int? Score(string status) =>
        status.Length > 1 && int.TryParse(status[1..], out var score) ? score : null;
}
