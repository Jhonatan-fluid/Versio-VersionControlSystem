using System.Text;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;
using Versio.Web.Review;
using Versio.Web.Sources;

namespace Versio.Web.Api;

/// <param name="Comments">The review comments whose suggestions are applied, in one commit: one, or a batch.</param>
public sealed record ApplySuggestionsRequest(string? OpKey, string[]? Comments) : WriteRequest(OpKey);

/// <summary>
/// Applying suggested changes (docs/web/FINISH.md, Step 5): the <c>```suggestion</c> blocks of one or
/// several review comments, committed to the head branch as one commit — GitHub's "Apply suggestion"
/// and "Commit suggestions". The merge tier (S11), because it is the one other write that changes a
/// branch, and it needs Contents write exactly as a merge does.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is applied is what was reviewed.</b> Every suggestion's lines are read from the snapshot's
/// head, and the commit carries that head as GitHub's atomic <c>expectedHeadOid</c>: a push in
/// between makes GitHub refuse, so a suggestion written for one version of a line is never applied to
/// another. The server's own staleness check narrows the window; GitHub's precondition closes it.
/// </para>
/// <para>
/// <b>A suggestion goes only where its thread is now.</b> The thread must be on the new side, on a
/// line (not the whole file), and not outdated: an outdated thread's lines moved, and GitHub's own
/// page refuses to apply it too. Two suggestions on overlapping lines of one file are refused rather
/// than guessed at — GitHub's batch refuses them as well.
/// </para>
/// <para>
/// <b>The commit says it is Versio's</b>: the operation tag rides in the message body, which is how a
/// lost answer is settled — the live head is read, and it is this operation's commit only if its
/// first parent is the reviewed head and its message carries the tag. The suggestions' authors are
/// credited as GitHub credits them, with <c>Co-authored-by</c> trailers.
/// </para>
/// <para>
/// <b>A fork's branch is refused.</b> The commit would have to land in the fork, which the App is
/// not installed on; GitHub's page can do it with the author's permission, and this one says why it
/// does not.
/// </para>
/// </remarks>
public static partial class PullWriteEndpoints
{
    /// <summary>At most this many suggestions in one commit: a page has no reason to batch more.</summary>
    private const int MaxSuggestions = 50;

    private static void MapSuggestions(RouteGroupBuilder pulls)
    {
        pulls.MapWrite("POST", "/suggestions/apply", async (string owner, string repo, int number, ApplySuggestionsRequest request, HttpContext http, Writes w, IOptions<WebOptions> options, CancellationToken ct) =>
        {
            var comments = request.Comments ?? [];
            if (comments.Length is 0 or > MaxSuggestions || comments.Distinct(StringComparer.Ordinal).Count() != comments.Length || comments.Any(c => !NodeId().IsMatch(c)))
            {
                return Invalid("comments", "Name the suggestions to apply, up to fifty, each once.");
            }

            (string Branch, string Message, IReadOnlyList<FileChange> Files)? planned = null;
            return await w.RunAsync(http, owner, repo, number, request, "apply-suggestions", ReviewTags.PayloadHash(comments),
                precondition: async (who, s) =>
                {
                    var (refusal, plan) = await w.SuggestionsPreconditionAsync(http, who, s, comments, options.Value.MaxBlobBytes, ct).ConfigureAwait(false);
                    planned = plan;
                    return refusal;
                },
                (who, s, key) => w.Writer.CommitFilesAsync(who, owner, repo, planned!.Value.Branch, s.HeadSha,
                    planned.Value.Message + "\n\n" + ReviewTags.OperationTag(key), planned.Value.Files, CancellationToken.None),
                // The live head is this operation's commit only if it was made on the reviewed head
                // and says so: anyone else's push since has another parent or another message.
                async (who, s, key, since) =>
                {
                    var live = await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false);
                    if (!live.Ok)
                    {
                        return live.As<string>();
                    }

                    if (live.Value!.HeadSha == s.HeadSha)
                    {
                        return RepoResult<string>.Of("");
                    }

                    var parents = await w.Source.GetCommitParentsAsync(who, owner, repo, live.Value.HeadSha, ct).ConfigureAwait(false);
                    if (!parents.Ok)
                    {
                        return parents.As<string>();
                    }

                    if (parents.Value!.Count != 1 || parents.Value[0] != s.HeadSha)
                    {
                        return RepoResult<string>.Of("");
                    }

                    var message = await w.Source.GetCommitMessageAsync(who, owner, repo, live.Value.HeadSha, ct).ConfigureAwait(false);
                    return message.Ok ? RepoResult<string>.Of(ReviewTags.Carries(message.Value, key) ? live.Value.HeadSha : "") : message.As<string>();
                },
                idempotent: false, ct).ConfigureAwait(false);
        }).WithMetadata(new MergeEndpoint());
    }

    public sealed partial class Writes
    {
        /// <summary>
        /// Everything that must hold before a commit is made, and the commit itself worked out: the
        /// pull request still at the snapshot, the head a branch of this repository, every comment a
        /// review comment of this pull request with a suggestion, on a current new-side line, no two
        /// on the same lines, every file readable at the head.
        /// </summary>
        internal async Task<(IResult? Refusal, (string Branch, string Message, IReadOnlyList<FileChange> Files)? Plan)> SuggestionsPreconditionAsync(
            HttpContext http, RepoAccess who, ReviewSnapshot s, IReadOnlyList<string> comments, long maxBlobBytes, CancellationToken ct)
        {
            if (await StaleAsync(who, s, ct).ConfigureAwait(false) is { } stale)
            {
                return (stale, null);
            }

            var (refused, live) = await LiveAsync(who, s, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                return (refused, null);
            }

            if (live!.State != "OPEN")
            {
                return (Results.Json(new { error = "not-open", message = "Suggestions are applied only on an open pull request." }, statusCode: 409), null);
            }

            if (live.HeadRepository is not null)
            {
                return (Results.Json(new { error = "fork", message = "This branch lives in a fork, where Versio cannot commit. Apply the suggestion on GitHub." }, statusCode: 422), null);
            }

            var pull = await Source.GetPullAsync(who, s.Owner, s.Repo, s.Number, ct).ConfigureAwait(false);
            if (!pull.Ok)
            {
                return (ApiEndpoints.Refused(pull.Failure), null);
            }

            var threads = await Review.ThreadsAsync(who, s, ct).ConfigureAwait(false);
            if (!threads.Ok)
            {
                return (ApiEndpoints.Refused(threads.Failure), null);
            }

            // Each comment: found in a thread of this pull request (S7, by the read rather than a
            // node query — the thread is what places it), carrying a suggestion, on a line it can go to.
            var edits = new List<(string Path, int Start, int End, string Suggestion, string Author)>();
            foreach (var id in comments)
            {
                var thread = threads.Value!.Threads.FirstOrDefault(t => t.Comments.Any(c => c.Id == id));
                var comment = thread?.Comments.First(c => c.Id == id);
                if (thread is null || comment is null)
                {
                    Log.Warn(LogCategory.Web, $"write refused (S7: comment {id} is not in {s.Owner}/{s.Repo}#{s.Number})");
                    return (Results.Json(new { error = "not-found", message = "Not found, or not visible to you." }, statusCode: 404), null);
                }

                if (Suggestions.Read(comment.Body) is not { } suggestion)
                {
                    return (Results.Json(new { error = "no-suggestion", message = "That comment carries no suggested change." }, statusCode: 422), null);
                }

                if (thread.Outdated || thread.Side != "RIGHT" || thread.SubjectType != "LINE" || thread.Line is not { } line)
                {
                    return (Results.Json(new { error = "suggestion-not-applicable", message = thread.Outdated
                        ? "The lines this suggestion was written for have changed since; it cannot be applied as it is."
                        : "A suggestion applies only to current lines of the new file." }, statusCode: 422), null);
                }

                edits.Add((thread.Path, thread.StartLine ?? line, line, suggestion, comment.Author));
            }

            foreach (var group in edits.GroupBy(e => e.Path, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(e => e.Start).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].Start <= ordered[i - 1].End)
                    {
                        return (Results.Json(new { error = "suggestions-overlap", message = "Two of these suggestions change the same lines. Apply them one at a time." }, statusCode: 422), null);
                    }
                }
            }

            var model = await Load.ModelAsync(http, s.Owner, s.Repo, s.HeadSha, ct).ConfigureAwait(false);
            if (!model.Ok)
            {
                return (ApiEndpoints.Refused(model.Failure), null);
            }

            var files = new List<FileChange>();
            foreach (var group in edits.GroupBy(e => e.Path, StringComparer.Ordinal))
            {
                if (!model.Value.Model.Files.TryGetValue(group.Key, out var entry))
                {
                    return (Results.Json(new { error = "file-not-found", message = "The file is not in the reviewed commit." }, statusCode: 422), null);
                }

                var bytes = await Source.GetBlobAsync(who, s.Owner, s.Repo, entry.BlobSha, maxBlobBytes, ct).ConfigureAwait(false);
                if (!bytes.Ok)
                {
                    return (ApiEndpoints.Refused(bytes.Failure), null);
                }

                // The file must come back from text as the bytes it was: a file that is not UTF-8
                // (a Windows-1252 degree sign in a comment, a UTF-16 export) decodes with U+FFFD in
                // place of every bad byte, and committing that rewrites the whole file — the noise
                // Suggestions.cs exists to avoid, and a loss of the characters (review, 2026-10-24).
                // A BOM round-trips (GetString keeps it as U+FEFF), so a file with one is still fine.
                var raw = bytes.Value!;
                var text = Encoding.UTF8.GetString(raw);
                if (!Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(raw))
                {
                    Log.Info(LogCategory.Web, $"suggestion refused: {s.Owner}/{s.Repo}#{s.Number} {group.Key} is not UTF-8 text");
                    return (Results.Json(new { error = "file-not-utf8", message = "The file is not UTF-8 text, so a suggestion is not applied to it from here." }, statusCode: 422), null);
                }

                // The BOM is not part of line 1: GetString keeps it as U+FEFF at the front of the
                // text, and a suggestion that replaces line 1 would have dropped it with the line —
                // a three-byte change at the file's start the reviewer never saw as text (review of
                // the review, 2026-10-24). Held aside, put back after.
                var bom = text.Length > 0 && text[0] == '\uFEFF';
                if (bom)
                {
                    text = text[1..];
                }

                // Applied from the bottom up, so an earlier edit's line count never shifts a later one.
                foreach (var edit in group.OrderByDescending(e => e.Start))
                {
                    if (Suggestions.Apply(text, edit.Start, edit.End, edit.Suggestion) is not { } applied)
                    {
                        return (Results.Json(new { error = "suggestion-not-applicable", message = "The suggestion's lines are not in the file as reviewed." }, statusCode: 422), null);
                    }

                    text = applied;
                }

                files.Add(new FileChange(group.Key, Encoding.UTF8.GetBytes(bom ? "\uFEFF" + text : text)));
            }

            // GitHub's own message for the same action, with its co-author trailers: the suggestions'
            // authors other than the person committing.
            var message = new StringBuilder(edits.Count == 1 ? "Apply suggestion from code review" : "Apply suggestions from code review");
            foreach (var author in edits.Select(e => e.Author).Where(a => a != who.Who && a.Length > 0).Distinct(StringComparer.Ordinal))
            {
                message.Append("\n\nCo-authored-by: ").Append(author).Append(" <").Append(author).Append("@users.noreply.github.com>");
            }

            return (null, (pull.Value!.Pull.HeadRef, message.ToString(), files));
        }
    }
}
