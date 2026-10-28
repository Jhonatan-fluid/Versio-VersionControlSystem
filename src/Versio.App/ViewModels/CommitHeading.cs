using System.Globalization;
using Versio.Core.Diff;
using Versio.Core.Git;
using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// What sits above a commit's patch: the rest of its message, who and when, and what it touched.
/// </summary>
/// <remarks>
/// <para>
/// A commit opened as its patch alone. The subject was in the pane header, and the body - which
/// in this repository is where the reasoning is - was nowhere in the app; nor was who wrote it,
/// when, or how big it was, without scrolling the whole patch. Seen in the running window on
/// 2026-09-21.
/// </para>
/// <para>
/// <see cref="Objects"/> is the git panel's own sentence ("3 POUs changed in RefrigeratorControl,
/// plus 2 other files"), asked of the files this commit touched. Since 2026-10-28 it is read
/// against the commit's own index (<c>CommitSnapshot.Objects</c>, the manifest as it was in that
/// commit), so an object since renamed or deleted is still counted as what it was. Until then
/// it was read against today's index - the honest reading while there was no other, and the
/// same "today's name on yesterday's file" the history's rows were avoiding by naming nothing.
/// </para>
/// </remarks>
/// <param name="Body">The message after its subject line, trimmed; empty when there is none.</param>
/// <param name="Byline">Author and local date: <c>jhonatanevm · 2026-09-20 20:31</c>.</param>
/// <param name="Stats">Files and lines: <c>3 files · +42 −7</c>.</param>
/// <param name="Objects">The object sentence, or empty when nothing touched is a PLC object.</param>
public sealed record CommitHeading(string Body, string Byline, string Stats, string Objects)
{
    public bool HasBody => Body.Length > 0;

    public bool HasObjects => Objects.Length > 0;

    public static CommitHeading Of(
        CommitEntry? commit, string? message, DiffDocument patch, ObjectIndex index)
    {
        var body = BodyOf(message);

        var byline = commit is null
            ? string.Empty
            : commit.When is { } when
                ? $"{commit.Author} · {when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
                : commit.Author;

        var files = patch.Files.Count;
        var stats = $"{files} {(files == 1 ? "file" : "files")} · +{patch.Added} −{patch.Removed}";

        var changes = patch.Files
            .Select(f => new FileChange
            {
                Path = f.Path,
                Staged = f.Change switch
                {
                    DiffFileChange.Added => ChangeKind.Added,
                    DiffFileChange.Deleted => ChangeKind.Deleted,
                    DiffFileChange.Renamed => ChangeKind.Renamed,
                    _ => ChangeKind.Modified,
                },
                Unstaged = ChangeKind.Unmodified,
                OriginalPath = f.OldPath,
            })
            .ToList();

        return new CommitHeading(body, byline, stats, ChangeSummary.Sentence(changes, index));
    }

    /// <summary>Everything after the subject and the blank line that follows it.</summary>
    private static string BodyOf(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normal = message.Replace("\r\n", "\n");
        var firstBreak = normal.IndexOf('\n');
        return firstBreak < 0 ? string.Empty : normal[(firstBreak + 1)..].Trim();
    }
}
