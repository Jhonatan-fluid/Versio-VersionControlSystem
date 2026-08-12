using System.Globalization;

namespace Versio.Core.Git;

/// <summary>One commit, as the history list shows it.</summary>
public sealed record CommitEntry
{
    public required string Id { get; init; }

    /// <summary>Abbreviated id, which is what a person quotes.</summary>
    public required string ShortId { get; init; }

    public required string Subject { get; init; }

    public string Author { get; init; } = string.Empty;

    /// <summary>Author date, or null when git wrote something unparseable.</summary>
    public DateTimeOffset? When { get; init; }

    /// <summary>Git's own relative phrasing — "2 hours ago".</summary>
    /// <remarks>
    /// Taken from git rather than computed here, so the list agrees with what the same
    /// repository says in a terminal. Rounding "yesterday" differently from `git log` is the
    /// kind of small disagreement that makes a tool feel like it is guessing.
    /// </remarks>
    public string Age { get; init; } = string.Empty;
}

/// <summary>One entry in the stash.</summary>
public sealed record StashEntry
{
    /// <summary>The name git accepts back — <c>stash@{0}</c>.</summary>
    /// <remarks>
    /// <b>Positional, and it moves.</b> Dropping <c>stash@{0}</c> renumbers everything below
    /// it, so a reference held across a refresh can name a different stash than the one the
    /// user was looking at. Anything acting on one of these re-reads the list first.
    /// </remarks>
    public required string Ref { get; init; }

    /// <summary>Git's own one-line description of the entry.</summary>
    public required string Subject { get; init; }

    /// <summary>The branch the stash was taken on, when the subject names one.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>The readable part of the subject, without git's bookkeeping.</summary>
    public string Message { get; init; } = string.Empty;

    public string Age { get; init; } = string.Empty;
}

/// <summary>
/// Parses the two <c>git log</c>-shaped listings the shell needs: history and the stash.
/// </summary>
/// <remarks>
/// <para>
/// Both are read with an explicit <c>--format</c> using ASCII unit and record separators
/// rather than a delimiter that could occur in the data. A commit subject may contain tabs,
/// pipes, newlines and anything else somebody typed; <c>0x1F</c> and <c>0x1E</c> cannot appear
/// in one, which is what they are for.
/// </para>
/// <para>
/// Pure, and separate from the client that runs git, for the same reason porcelain v2 parsing
/// is: the shapes are then tested against real output without a repository.
/// </para>
/// </remarks>
public static class GitLogParser
{
    /// <summary>Field separator inside one record.</summary>
    public const char Unit = '\u001f';

    /// <summary>Record separator between entries.</summary>
    public const char Record = '\u001e';

    /// <summary>The <c>--format</c> that produces what <see cref="ParseCommits"/> reads.</summary>
    /// <remarks>
    /// The separators are written as git's own <c>%x1f</c> / <c>%x1e</c> escapes rather than
    /// as raw bytes in the argument. Both work, but a control character has to survive
    /// <c>ProcessStartInfo</c>, the Windows command line and whatever encoding sits between —
    /// and an escape git expands itself has none of that to survive.
    /// </remarks>
    public const string CommitFormat = "--format=%H%x1f%h%x1f%s%x1f%an%x1f%aI%x1f%ar%x1e";

    /// <summary>The <c>--format</c> that produces what <see cref="ParseStashes"/> reads.</summary>
    public const string StashFormat = "--format=%gd%x1f%gs%x1f%cr%x1e";

    public static IReadOnlyList<CommitEntry> ParseCommits(string? output)
    {
        var commits = new List<CommitEntry>();

        foreach (var fields in Records(output, expected: 6))
        {
            commits.Add(new CommitEntry
            {
                Id = fields[0],
                ShortId = fields[1],
                Subject = fields[2],
                Author = fields[3],
                When = ParseDate(fields[4]),
                Age = fields[5],
            });
        }

        return commits;
    }

    public static IReadOnlyList<StashEntry> ParseStashes(string? output)
    {
        var stashes = new List<StashEntry>();

        foreach (var fields in Records(output, expected: 3))
        {
            var (branch, message) = SplitSubject(fields[1]);

            stashes.Add(new StashEntry
            {
                Ref = fields[0],
                Subject = fields[1],
                Branch = branch,
                Message = message,
                Age = fields[2],
            });
        }

        return stashes;
    }

    /// <summary>
    /// Pull the branch and the readable message out of a stash subject.
    /// </summary>
    /// <remarks>
    /// Git writes two shapes: <c>WIP on main: 1a2b3c4 last commit subject</c> for a plain
    /// <c>git stash</c>, and <c>On main: whatever the user typed</c> when they supplied a
    /// message. The first is bookkeeping — the commit it names is the one the stash was taken
    /// against, not a description of the work — so the abbreviated id is dropped and what
    /// remains is at least a hint. A subject in neither shape is passed through whole rather
    /// than mangled.
    /// </remarks>
    private static (string Branch, string Message) SplitSubject(string subject)
    {
        var colon = subject.IndexOf(':');
        if (colon < 0)
        {
            return (string.Empty, subject);
        }

        var head = subject[..colon];
        var rest = subject[(colon + 1)..].Trim();

        if (head.StartsWith("WIP on ", StringComparison.Ordinal))
        {
            var space = rest.IndexOf(' ');
            return (head["WIP on ".Length..],
                    space > 0 && LooksLikeCommitId(rest[..space]) ? rest[(space + 1)..] : rest);
        }

        if (head.StartsWith("On ", StringComparison.Ordinal))
        {
            return (head["On ".Length..], rest);
        }

        return (string.Empty, subject);
    }

    private static bool LooksLikeCommitId(string token) =>
        token.Length is >= 4 and <= 40 && token.All(Uri.IsHexDigit);

    /// <summary>Split the output into records, skipping anything short of the field count.</summary>
    /// <remarks>
    /// A malformed record is dropped rather than thrown over. This drives a list somebody
    /// glances at; a history panel that takes the window down because one commit subject did
    /// something unexpected would be a far worse failure than a missing row.
    /// </remarks>
    private static IEnumerable<string[]> Records(string? output, int expected)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            yield break;
        }

        foreach (var record in output.Split(Record))
        {
            // Git separates records with our terminator and its own newline, so each record
            // after the first arrives with a leading line break.
            var trimmed = record.Trim('\n', '\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var fields = trimmed.Split(Unit);
            if (fields.Length >= expected)
            {
                yield return fields;
            }
        }
    }

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
