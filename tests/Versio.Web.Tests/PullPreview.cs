using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Auth;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Not a test: the review page against ONE REAL pull request, dumped from GitHub to disk, so the
/// whole review loop can be looked at on real PLC code without registering an App. Does nothing
/// unless VERSIO_WEB_PREVIEW_PULL names a dump directory.
/// </summary>
/// <remarks>
/// <para>
/// The fixture preview shows every feature on made-up changes; this shows what a real change looks
/// like — its file count, its renames, the files GitHub sends no patch for, the review it actually
/// got. The dump (trees at base and head, GitHub's patches, threads, reviews, checks) is read-only
/// GitHub data written by <c>tools/web/dump_pull.py</c>; nothing here talks to GitHub. It is how
/// the owner saw the web's Rendered tab disagree with the app's on a real library (2026-10-23).
/// </para>
/// <para>
/// Writes go to an in-memory writer that also files each one back into the fake repository, so a
/// comment, reply, resolution or review appears on the page once posted — the fixture preview's
/// writer records them only. Two sessions are written to
/// %TEMP%/versio-web-pull-preview-sessions.txt: the pull request's author, and a reviewer who can
/// approve (GitHub never lets an author approve their own). It serves on port 5199 for an hour.
/// </para>
/// <para>
/// VERSIO_WEB_PREVIEW_PORT moves it to another port, and the sessions file then carries the port in
/// its name, so a second preview (a worktree checking a change against the same dump while the owner's
/// own preview holds 5199, 2026-10-23) neither fails to bind nor overwrites the owner's session ids.
/// </para>
/// </remarks>
public sealed class PullPreview
{
    public const string ReviewerLogin = "demo-reviewer";

    [Fact]
    public async Task Serve_a_dumped_pull_request_on_port_5199_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW_PULL");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "pull.json")));
        var d = json.RootElement;
        string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";
        DateTimeOffset When(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.GetString() is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;

        var owner = S(d, "owner");
        var name = S(d, "name");
        var number = d.GetProperty("number").GetInt32();
        var author = S(d, "author");
        var github = new FakeGitHub();
        var repo = github.Add(owner, name);

        Dictionary<string, byte[]> Tree(string which)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(Path.Combine(dir, which + ".tree")))
            {
                var tab = line.IndexOf('\t', StringComparison.Ordinal);
                files[line[(tab + 1)..]] = File.ReadAllBytes(Path.Combine(dir, "blobs", line[..tab]));
            }

            return files;
        }

        // The fake names its own commit ids, so every sha the page sees is the fake's, never GitHub's.
        var baseRef = S(d, "baseRef");
        var headRef = S(d, "headRef");
        var baseSha = github.Commit(repo, baseRef, Tree("base"));
        var mergeBase = baseSha;
        if (File.Exists(Path.Combine(dir, "merge-base.tree")))
        {
            // The base moved after branching: the merge-base is its own commit, on no branch.
            mergeBase = github.Commit(repo, "versio-preview-merge-base", Tree("merge-base"));
            repo.Refs.Remove("versio-preview-merge-base");
        }

        var head = github.Commit(repo, headRef, Tree("head"));
        repo.MergeBases[(baseSha, head)] = mergeBase;

        repo.Pulls.Add((new PullInfo(number, S(d, "title"), author, When(d, "updated"), baseRef, headRef, head, d.GetProperty("draft").GetBoolean()), S(d, "body")));
        var files = d.GetProperty("files").EnumerateArray()
            .Select(f => new PullFile(S(f, "path"), S(f, "status"), f.GetProperty("previous").GetString(), f.GetProperty("patch").GetString()))
            .ToList();
        repo.PullFiles[number] = new PullFileList(files, false);
        repo.CompareFiles[(mergeBase, head)] = new PullFileList(files, false);

        var commits = d.GetProperty("commits").EnumerateArray().ToList();
        // Every real commit but the last is history the fake does not hold; the last is the head.
        repo.PullCommits[number] = new PullCommitList(
            commits.Select((c, i) => new PullCommit(i == commits.Count - 1 ? head : S(c, "sha"), S(c, "headline"), S(c, "author"), When(c, "when"))).ToList(), false);

        var timeline = new List<TimelineItem>();
        timeline.AddRange(commits.Select((c, i) => new TimelineItem("commit", S(c, "author"), When(c, "when"), S(c, "headline"), null, i == commits.Count - 1 ? head : S(c, "sha"))));
        timeline.AddRange(d.GetProperty("comments").EnumerateArray().Select(c => new TimelineItem("comment", S(c, "author"), When(c, "when"), S(c, "body"), null, null, S(c, "id"))));
        var reviewers = new List<PullReviewer>();
        foreach (var r in d.GetProperty("reviews").EnumerateArray())
        {
            timeline.Add(new TimelineItem("review", S(r, "author"), When(r, "when"), S(r, "body"), S(r, "state"), head));
            reviewers.RemoveAll(x => x.Login == S(r, "author"));
            reviewers.Add(new PullReviewer(S(r, "author"), S(r, "state")));
        }

        reviewers.AddRange(d.GetProperty("requested").EnumerateArray().Select(x => new PullReviewer(x.GetString()!, "REQUESTED")));
        repo.Timelines[number] = new Timeline(timeline.OrderBy(t => t.When).ToList(), false);

        var labels = d.GetProperty("labels").EnumerateArray().Select(l => new PullLabel(S(l, "name"), S(l, "color"))).ToList();
        repo.PullPeople[number] = (labels, reviewers);
        repo.Labels.AddRange(d.GetProperty("repoLabels").EnumerateArray().Select(l => new PullLabel(S(l, "name"), S(l, "color"))));
        if (repo.Labels.Count == 0)
        {
            // The repository has none; three so the label editor has something to offer.
            repo.Labels.AddRange([new PullLabel("library", "1f6feb"), new PullLabel("needs-plc-test", "9a6700"), new PullLabel("standard", "1a7f37")]);
        }

        repo.Assignees[number] = d.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()!).ToList();
        repo.Assignable.UnionWith([author, ReviewerLogin, .. reviewers.Select(r => r.Login)]);
        repo.Checks[head] = d.GetProperty("checks").EnumerateArray()
            .Select(c => new CheckInfo(S(c, "name"), S(c, "status"), c.GetProperty("conclusion").GetString(), c.GetProperty("url").GetString(), S(c, "source"))).ToList();
        repo.MergeStatus[number] = "CLEAN";

        var writer = new FakeWriter { Repo = repo };
        var threads = d.GetProperty("threads").EnumerateArray().Select(t => new ReviewThread(
            S(t, "id"), S(t, "path"), t.GetProperty("resolved").GetBoolean(), t.GetProperty("outdated").GetBoolean(),
            Int(t, "line"), Int(t, "startLine"), S(t, "side"), Int(t, "originalLine"), S(t, "subjectType"),
            t.GetProperty("comments").EnumerateArray().Select(c => new ReviewComment(S(c, "id"), S(c, "author"), S(c, "body"), When(c, "when"), head, head)).ToList(),
            false, CanReply: true, CanResolve: !t.GetProperty("resolved").GetBoolean(), CanUnresolve: t.GetProperty("resolved").GetBoolean())).ToList();
        foreach (var t in threads)
        {
            writer.Threads[t.Id] = ($"{owner}/{name}", number, t.Resolved);
        }

        repo.Threads[number] = new ReviewThreadList(threads, false);
        github.PreviewGraphWithoutToken = true;

        var port = Environment.GetEnvironmentVariable("VERSIO_WEB_PREVIEW_PORT") is { Length: > 0 } chosen ? chosen : "5199";
        var settings = new Dictionary<string, string?>
        {
            ["urls"] = "http://127.0.0.1:" + port,
            ["Web:Repositories:0"] = $"{owner}/{name}",
            ["Web:Writes:Enabled"] = "true",
            ["Web:Writes:Repositories:0"] = $"{owner}/{name}",
            ["Web:Writes:SingleInstance"] = "true",
            ["Web:Writes:Merge"] = "true",
            // A person clicking through the whole loop writes faster than the production limit allows.
            ["Web:Writes:PerSessionPerMinute"] = "120",
        };

        var simulated = new FilingWriter(writer, repo, head);
        await using var host = await TestHost.StartAsync(github, settings, s => s.AddSingleton<IRepoWriter>(simulated));
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var asAuthor = await sessions.CreateAsync(11, author, author, "", "author-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(1));
        var asReviewer = await sessions.CreateAsync(12, ReviewerLogin, ReviewerLogin, "", "reviewer-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(1));
        repo.PullAuthorToken[number] = "author-token";
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), port == "5199" ? "versio-web-pull-preview-sessions.txt" : $"versio-web-pull-preview-sessions-{port}.txt"),
            $"author {asAuthor.Id}\nreviewer {asReviewer.Id}\n");

        await Task.Delay(TimeSpan.FromHours(1));
    }

    private static int? Int(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Number ? e.GetProperty(name).GetInt32() : null;

    /// <summary>
    /// The fake writer, plus filing: each write it does is also put where the reads look, so the page
    /// shows what was posted the way GitHub would after a reload. Only for looking at; the tests use
    /// <see cref="FakeWriter"/> directly, where a write and its later read are kept apart on purpose.
    /// </summary>
    private sealed class FilingWriter(FakeWriter inner, FakeGitHub.FakeRepo store, string head) : IRepoWriter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, LineComment> _pendingLines = new(StringComparer.Ordinal);

        private static DateTimeOffset Now => DateTimeOffset.UtcNow;

        private void Timeline(int number, Func<List<TimelineItem>, List<TimelineItem>> change)
        {
            lock (_gate)
            {
                var now = store.Timelines.TryGetValue(number, out var t) ? t.Items.ToList() : [];
                store.Timelines[number] = new Timeline(change(now), false);
            }
        }

        private void Threads(int number, Func<List<ReviewThread>, List<ReviewThread>> change)
        {
            lock (_gate)
            {
                var now = store.Threads.TryGetValue(number, out var t) ? t.Threads.ToList() : [];
                store.Threads[number] = new ReviewThreadList(change(now), false);
            }
        }

        private void NewThread(string owner, string name, int number, string commentId, string who, LineComment line)
        {
            var id = "T_" + commentId;
            inner.Threads[id] = ($"{owner}/{name}", number, false);
            Threads(number, t => [.. t, new ReviewThread(id, line.Path, false, false, line.Line, line.StartLine, line.Side, line.Line, line.Line is null ? "FILE" : "LINE",
                [new ReviewComment(commentId, who, line.Body, Now, head, head, CanEdit: true, CanDelete: true)], false, CanReply: true, CanResolve: true)]);
        }

        private void Reviewed(int number, RepoAccess access, string state, string body, string commit)
        {
            Timeline(number, t => [.. t, new TimelineItem("review", access.Who, Now, body, state, commit)]);
            lock (_gate)
            {
                var people = store.PullPeople.TryGetValue(number, out var p) ? p : ([], []);
                store.PullPeople[number] = (people.Labels, [.. people.Reviewers.Where(r => r.Login != access.Who), new PullReviewer(access.Who, state)]);
                store.LastReviewCommit[(number, access.Token!)] = commit;
            }
        }

        private static string StateOf(string reviewEvent) => reviewEvent switch
        {
            "APPROVE" => "APPROVED",
            "REQUEST_CHANGES" => "CHANGES_REQUESTED",
            _ => "COMMENTED",
        };

        public async Task<WriteResult<string>> AddConversationCommentAsync(RepoAccess access, string owner, string repo, int number, string body, CancellationToken ct)
        {
            var r = await inner.AddConversationCommentAsync(access, owner, repo, number, body, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                Timeline(number, t => [.. t, new TimelineItem("comment", access.Who, Now, body, null, null, r.Value, CanEdit: true, CanDelete: true)]);
            }

            return r;
        }

        public async Task<WriteResult<string>> AddLineCommentAsync(RepoAccess access, string owner, string repo, int number, LineComment comment, CancellationToken ct)
        {
            var r = await inner.AddLineCommentAsync(access, owner, repo, number, comment, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                NewThread(owner, repo, number, r.Value!, access.Who, comment);
            }

            return r;
        }

        public async Task<WriteResult<string>> ReplyAsync(RepoAccess access, string threadId, string body, CancellationToken ct)
        {
            var r = await inner.ReplyAsync(access, threadId, body, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                Threads(inner.Threads[threadId].Number, t => t.Select(x => x.Id != threadId ? x
                    : x with { Comments = [.. x.Comments, new ReviewComment(r.Value!, access.Who, body, Now, head, head, CanEdit: true, CanDelete: true)] }).ToList());
            }

            return r;
        }

        public async Task<WriteResult<bool>> EditCommentAsync(RepoAccess access, string commentId, string type, string body, CancellationToken ct)
        {
            var r = await inner.EditCommentAsync(access, commentId, type, body, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                foreach (var number in store.Pulls.Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => t.Select(x => x.Id == commentId ? x with { Body = body } : x).ToList());
                    Threads(number, t => t.Select(x => x with { Comments = x.Comments.Select(c => c.Id == commentId ? c with { Body = body } : c).ToList() }).ToList());
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> DeleteCommentAsync(RepoAccess access, string commentId, string type, CancellationToken ct)
        {
            var r = await inner.DeleteCommentAsync(access, commentId, type, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                foreach (var number in store.Pulls.Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => t.Where(x => x.Id != commentId).ToList());
                    Threads(number, t => t.Select(x => x with { Comments = x.Comments.Where(c => c.Id != commentId).ToList() }).Where(x => x.Comments.Count > 0).ToList());
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> SetThreadResolvedAsync(RepoAccess access, string threadId, bool resolved, CancellationToken ct)
        {
            var r = await inner.SetThreadResolvedAsync(access, threadId, resolved, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                Threads(inner.Threads[threadId].Number, t => t.Select(x => x.Id != threadId ? x
                    : x with { Resolved = resolved, CanResolve = !resolved, CanUnresolve = resolved }).ToList());
            }

            return r;
        }

        public async Task<WriteResult<string>> SubmitReviewAsync(RepoAccess access, string owner, string repo, int number, string commit, string reviewEvent, string body, CancellationToken ct)
        {
            var r = await inner.SubmitReviewAsync(access, owner, repo, number, commit, reviewEvent, body, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                Reviewed(number, access, StateOf(reviewEvent), body, commit);
            }

            return r;
        }

        public Task<WriteResult<string>> StartPendingReviewAsync(RepoAccess access, string pullNodeId, string commit, CancellationToken ct) =>
            inner.StartPendingReviewAsync(access, pullNodeId, commit, ct);

        public async Task<WriteResult<string>> AddPendingThreadAsync(RepoAccess access, string reviewId, LineComment comment, CancellationToken ct)
        {
            var r = await inner.AddPendingThreadAsync(access, reviewId, comment, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                lock (_gate)
                {
                    _pendingLines[r.Value!] = comment;
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> SubmitPendingReviewAsync(RepoAccess access, string reviewId, string reviewEvent, string body, CancellationToken ct)
        {
            var node = inner.ReviewNodes[reviewId];
            var pending = store.Pending.TryGetValue((node.Number, node.Token), out var p) ? p : null;
            var r = await inner.SubmitPendingReviewAsync(access, reviewId, reviewEvent, body, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                // Submitted, the review's drafts become threads, as on GitHub.
                foreach (var c in pending?.Comments ?? [])
                {
                    LineComment? line;
                    lock (_gate)
                    {
                        _pendingLines.TryGetValue(c.Id, out line);
                    }

                    NewThread(store.Owner, store.Name, node.Number, c.Id, access.Who, line ?? new LineComment(node.Commit, c.Path, null, "RIGHT", null, c.Body));
                }

                Reviewed(node.Number, access, StateOf(reviewEvent), body, node.Commit);
            }

            return r;
        }

        public Task<WriteResult<bool>> DeletePendingReviewAsync(RepoAccess access, string reviewId, CancellationToken ct) => inner.DeletePendingReviewAsync(access, reviewId, ct);

        public async Task<WriteResult<bool>> SetReviewersAsync(RepoAccess access, string owner, string repo, int number, ReviewerChange change, CancellationToken ct)
        {
            var r = await inner.SetReviewersAsync(access, owner, repo, number, change, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                lock (_gate)
                {
                    var people = store.PullPeople.TryGetValue(number, out var p) ? p : ([], []);
                    store.PullPeople[number] = (people.Labels,
                    [
                        .. people.Reviewers.Where(x => !change.Remove.Contains(x.Login) && !change.Add.Contains(x.Login) && !(x.Slug is { } s && (change.RemoveTeams.Contains(s) || change.AddTeams.Contains(s)))),
                        .. change.Add.Select(a => new PullReviewer(a, "REQUESTED")),
                        .. change.AddTeams.Select(t => new PullReviewer(t, "REQUESTED", true) { Slug = t }),
                    ]);
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> DismissReviewAsync(RepoAccess access, string reviewId, string message, CancellationToken ct)
        {
            var r = await inner.DismissReviewAsync(access, reviewId, message, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                var node = inner.ReviewNodes[reviewId];
                Timeline(node.Number, t => [.. t.Select(x => x.Id == reviewId ? x with { State = "DISMISSED" } : x), new TimelineItem("review-dismissed", access.Who, Now, null, null, null) { Target = node.Author, Message = message }]);
                lock (_gate)
                {
                    var people = store.PullPeople.TryGetValue(node.Number, out var p) ? p : ([], []);
                    store.PullPeople[node.Number] = (people.Labels, people.Reviewers.Select(x => x.Login == node.Author ? x with { State = "DISMISSED" } : x).ToList());
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> EditReviewAsync(RepoAccess access, string reviewId, string body, CancellationToken ct)
        {
            var r = await inner.EditReviewAsync(access, reviewId, body, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                foreach (var number in store.Pulls.Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => t.Select(x => x.Id == reviewId ? x with { Body = body, EditedAt = Now } : x).ToList());
                }
            }

            return r;
        }

        /// <summary>A reaction filed onto whatever carries the subject id: the description, a comment, a review, a line comment.</summary>
        public async Task<WriteResult<bool>> SetReactionAsync(RepoAccess access, string subjectId, string content, bool on, CancellationToken ct)
        {
            var r = await inner.SetReactionAsync(access, subjectId, content, on, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                IReadOnlyList<ReactionGroup> Changed(IReadOnlyList<ReactionGroup> groups)
                {
                    var rest = groups.Where(g => g.Content != content).ToList();
                    var was = groups.FirstOrDefault(g => g.Content == content);
                    var count = (was?.Count ?? 0) + (on ? (was?.ViewerHasReacted == true ? 0 : 1) : (was?.ViewerHasReacted == true ? -1 : 0));
                    return count > 0 ? [.. rest, new ReactionGroup(content, count, on)] : rest;
                }

                foreach (var number in store.Pulls.Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => t.Select(x => x.Id == subjectId ? x with { Reactions = Changed(x.Reactions) } : x).ToList());
                    Threads(number, t => t.Select(x => x with { Comments = x.Comments.Select(c => c.Id == subjectId ? c with { Reactions = Changed(c.Reactions) } : c).ToList() }).ToList());
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> SetMinimizedAsync(RepoAccess access, string commentId, string? classifier, CancellationToken ct)
        {
            var r = await inner.SetMinimizedAsync(access, commentId, classifier, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                foreach (var number in store.Pulls.Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => t.Select(x => x.Id == commentId ? x with { Minimized = classifier is not null, MinimizedReason = classifier } : x).ToList());
                    Threads(number, t => t.Select(x => x with { Comments = x.Comments.Select(c => c.Id == commentId ? c with { Minimized = classifier is not null, MinimizedReason = classifier } : c).ToList() }).ToList());
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> SetLockedAsync(RepoAccess access, string pullNodeId, bool locked, string? reason, CancellationToken ct)
        {
            var r = await inner.SetLockedAsync(access, pullNodeId, locked, reason, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                var number = int.Parse(pullNodeId["PR_".Length..], System.Globalization.CultureInfo.InvariantCulture);
                Timeline(number, t => [.. t, new TimelineItem(locked ? "locked" : "unlocked", access.Who, Now, null, null, null) { Message = reason }]);
            }

            return r;
        }

        public async Task<WriteResult<bool>> SetMilestoneAsync(RepoAccess access, string owner, string repo, int number, int? milestone, CancellationToken ct)
        {
            var before = store.PullMilestone.TryGetValue(number, out var was) ? store.Milestones.FirstOrDefault(m => m.Number == was)?.Title : null;
            var r = await inner.SetMilestoneAsync(access, owner, repo, number, milestone, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                var after = milestone is { } m ? store.Milestones.FirstOrDefault(x => x.Number == m)?.Title : null;
                Timeline(number, t =>
                [
                    .. t,
                    .. before is null ? Array.Empty<TimelineItem>() : [new TimelineItem("demilestoned", access.Who, Now, null, null, null) { Target = before }],
                    .. after is null ? Array.Empty<TimelineItem>() : [new TimelineItem("milestoned", access.Who, Now, null, null, null) { Target = after }],
                ]);
            }

            return r;
        }

        public Task<WriteResult<bool>> SetSubscriptionAsync(RepoAccess access, string pullNodeId, string state, CancellationToken ct) => inner.SetSubscriptionAsync(access, pullNodeId, state, ct);

        public async Task<WriteResult<string>> CommitFilesAsync(RepoAccess access, string owner, string repo, string branch, string expectedHeadOid, string message, IReadOnlyList<FileChange> files, CancellationToken ct)
        {
            var r = await inner.CommitFilesAsync(access, owner, repo, branch, expectedHeadOid, message, files, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                foreach (var number in store.Pulls.Where(p => p.Info.HeadRef == branch).Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => [.. t, new TimelineItem("commit", access.Who, Now, message.Split('\n')[0], null, r.Value)]);
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> SetViewedAsync(RepoAccess access, string pullNodeId, string path, bool viewed, CancellationToken ct)
        {
            var r = await inner.SetViewedAsync(access, pullNodeId, path, viewed, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                lock (_gate)
                {
                    var number = int.Parse(pullNodeId["PR_".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    var marks = store.Viewed.TryGetValue((number, access.Token!), out var m) ? m : store.Viewed[(number, access.Token!)] = [];
                    marks.Remove(path);
                    if (viewed)
                    {
                        marks.Add(path);
                    }
                }
            }

            return r;
        }

        public Task<WriteResult<string>> MergePullAsync(RepoAccess access, string owner, string repo, int number, string headSha, string method, string? title, string? message, CancellationToken ct) =>
            inner.MergePullAsync(access, owner, repo, number, headSha, method, title, message, ct);

        public Task<WriteResult<bool>> UpdateBranchAsync(RepoAccess access, string owner, string repo, int number, string expectedHeadSha, string method, string pullNodeId, CancellationToken ct) =>
            inner.UpdateBranchAsync(access, owner, repo, number, expectedHeadSha, method, pullNodeId, ct);

        public async Task<WriteResult<bool>> DeleteBranchAsync(RepoAccess access, string owner, string repo, string branch, CancellationToken ct)
        {
            var r = await inner.DeleteBranchAsync(access, owner, repo, branch, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                foreach (var number in store.Pulls.Where(p => p.Info.HeadRef == branch).Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => [.. t, new TimelineItem("head-deleted", access.Who, Now, null, null, null) { Target = branch }]);
                }
            }

            return r;
        }

        public async Task<WriteResult<bool>> CreateBranchAsync(RepoAccess access, string owner, string repo, string branch, string sha, CancellationToken ct)
        {
            var r = await inner.CreateBranchAsync(access, owner, repo, branch, sha, ct);
            if (r.Outcome == WriteOutcome.Done)
            {
                foreach (var number in store.Pulls.Where(p => p.Info.HeadRef == branch).Select(p => p.Info.Number).ToList())
                {
                    Timeline(number, t => [.. t, new TimelineItem("head-restored", access.Who, Now, null, null, null)]);
                }
            }

            return r;
        }

        public Task<WriteResult<bool>> SetQueuedAsync(RepoAccess access, string pullNodeId, bool queued, string expectedHeadSha, CancellationToken ct) =>
            inner.SetQueuedAsync(access, pullNodeId, queued, expectedHeadSha, ct);

        public Task<WriteResult<bool>> EnableAutoMergeAsync(RepoAccess access, string pullNodeId, string method, string expectedHeadSha, CancellationToken ct) =>
            inner.EnableAutoMergeAsync(access, pullNodeId, method, expectedHeadSha, ct);

        public Task<WriteResult<bool>> DisableAutoMergeAsync(RepoAccess access, string pullNodeId, CancellationToken ct) => inner.DisableAutoMergeAsync(access, pullNodeId, ct);

        public Task<WriteResult<int>> CreatePullAsync(RepoAccess access, string owner, string repo, NewPull pull, CancellationToken ct) => inner.CreatePullAsync(access, owner, repo, pull, ct);

        public Task<WriteResult<bool>> EditPullAsync(RepoAccess access, string owner, string repo, int number, PullEdit edit, CancellationToken ct) => inner.EditPullAsync(access, owner, repo, number, edit, ct);

        public Task<WriteResult<bool>> SetPullOpenAsync(RepoAccess access, string owner, string repo, int number, bool open, CancellationToken ct) => inner.SetPullOpenAsync(access, owner, repo, number, open, ct);

        public Task<WriteResult<bool>> SetDraftAsync(RepoAccess access, string pullNodeId, bool draft, CancellationToken ct) => inner.SetDraftAsync(access, pullNodeId, draft, ct);

        public Task<WriteResult<bool>> SetLabelsAsync(RepoAccess access, string owner, string repo, int number, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct) =>
            inner.SetLabelsAsync(access, owner, repo, number, add, remove, ct);

        public Task<WriteResult<bool>> SetAssigneesAsync(RepoAccess access, string owner, string repo, int number, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct) =>
            inner.SetAssigneesAsync(access, owner, repo, number, add, remove, ct);

        public Task<RepoResult<int>> FindCreatedPullAsync(RepoAccess access, string owner, string repo, string head, string @base, string tag, CancellationToken ct) =>
            inner.FindCreatedPullAsync(access, owner, repo, head, @base, tag, ct);

        public Task<RepoResult<string>> FindTaggedReviewAsync(RepoAccess access, string owner, string repo, int number, string tag, CancellationToken ct) =>
            inner.FindTaggedReviewAsync(access, owner, repo, number, tag, ct);

        public Task<RepoResult<NodeOwner>> NodeOwnerAsync(RepoAccess access, string nodeId, CancellationToken ct) => inner.NodeOwnerAsync(access, nodeId, ct);

        public Task<RepoResult<string>> FindTaggedCommentAsync(RepoAccess access, string owner, string repo, int number, string tag, DateTimeOffset since, CancellationToken ct) =>
            inner.FindTaggedCommentAsync(access, owner, repo, number, tag, since, ct);

        public Task<RepoResult<string>> CommentBodyAsync(RepoAccess access, string commentId, CancellationToken ct) => inner.CommentBodyAsync(access, commentId, ct);

        public Task<RepoResult<bool>> ThreadResolvedAsync(RepoAccess access, string threadId, CancellationToken ct) => inner.ThreadResolvedAsync(access, threadId, ct);

        public Task<RepoResult<string>> ReviewBodyAsync(RepoAccess access, string reviewId, CancellationToken ct) => inner.ReviewBodyAsync(access, reviewId, ct);

        public Task<RepoResult<bool>> ViewerReactedAsync(RepoAccess access, string subjectId, string content, CancellationToken ct) => inner.ViewerReactedAsync(access, subjectId, content, ct);

        public Task<RepoResult<bool>> CommentMinimizedAsync(RepoAccess access, string commentId, CancellationToken ct) => inner.CommentMinimizedAsync(access, commentId, ct);
    }
}
