using System.Text.Json.Serialization;
using Versio.Core.Git;
using Versio.Core.Sync;

namespace Versio.Core.Guarding;

/// <summary>
/// A direction lock as it stood when an auto-stash was taken.
/// </summary>
/// <remarks>
/// <c>--include-untracked</c> excludes <i>ignored</i> files, so <c>.versio/local/</c> — the
/// locks, the backups, the scratch copies — does not travel with a stash. That is correct;
/// machine-local state should not be smuggled between branches inside one. But it means that
/// after stashing a dirty mirror the lock still claims <c>TextAuthoritative</c> with baselines
/// describing content no longer on disk, and the commit gate would report a mirror as stale
/// that is, on this branch, perfectly clean.
/// <para>
/// So taking a stash captures the affected locks here and releases them; restoring puts them
/// back. That is what makes it a round trip rather than a file copy — the working tree and the
/// sync state are both returned, and neither one alone is enough.
/// </para>
/// </remarks>
public sealed record StashedLock
{
    [JsonPropertyName("bindingId")] public required string BindingId { get; init; }

    [JsonPropertyName("direction")] public SyncDirection Direction { get; init; }

    [JsonPropertyName("projectHash")] public string ProjectHash { get; init; } = string.Empty;

    [JsonPropertyName("mirrorHash")] public string MirrorHash { get; init; } = string.Empty;
}

/// <summary>One stash Versio took on the user's behalf, as the sidecar records it.</summary>
/// <remarks>
/// <para>
/// <b>The sidecar is the authority; the stash is only the storage.</b> Three properties of
/// <c>git stash list</c> make it unusable as the record: <c>stash@{n}</c> is positional and
/// renumbers when anything below it is dropped; the subject is a human-readable string a user's
/// own stash can imitate exactly; and the stash carries no sync state at all.
/// </para>
/// <para>
/// The marker written into the stash message is a courtesy to somebody reading
/// <c>git stash list</c> in a terminal. It is never what a decision is made from.
/// </para>
/// </remarks>
public sealed record AutoStashRecord
{
    /// <summary>The branch it was taken on, or <c>detached@&lt;sha&gt;</c>.</summary>
    /// <seealso cref="AutoStash.KeyFor"/>
    [JsonPropertyName("branch")] public required string Branch { get; init; }

    /// <summary>Full SHA of the stash commit — the stable identity.</summary>
    [JsonPropertyName("stashCommitId")] public required string StashCommitId { get; init; }

    /// <summary>The marker written into the stash message.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;

    [JsonPropertyName("createdUtc")] public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>HEAD when the stash was taken.</summary>
    [JsonPropertyName("headCommit")] public string HeadCommit { get; init; } = string.Empty;

    [JsonPropertyName("includedUntracked")] public bool IncludedUntracked { get; init; } = true;

    /// <summary>How many files it holds, for a sentence that says what is at stake.</summary>
    [JsonPropertyName("fileCount")] public int FileCount { get; init; }

    /// <summary>The direction locks released when it was taken.</summary>
    /// <seealso cref="StashedLock"/>
    [JsonPropertyName("locks")] public IReadOnlyList<StashedLock> Locks { get; init; } = [];
}

/// <summary>The contents of <c>.versio/local/autostash.json</c>.</summary>
/// <remarks>
/// Machine-local and gitignored, beside <c>state.json</c>: a stash never leaves the clone it
/// was taken in, so a record of one has no business travelling either.
/// </remarks>
public sealed record AutoStashState
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;

    /// <summary>Every live record, newest first.</summary>
    /// <remarks>
    /// A flat list rather than a map keyed by branch, because a branch is not a valid JSON
    /// key in every shape git allows and the list is never long enough for the lookup cost to
    /// matter. Several records per branch is legitimate — switching away twice without coming
    /// back is an ordinary thing to do, and overwriting the first would strand its stash.
    /// </remarks>
    [JsonPropertyName("entries")] public IReadOnlyList<AutoStashRecord> Entries { get; init; } = [];

    public static readonly AutoStashState Empty = new();
}

/// <summary>An auto-stash that is still in git, with the ref git currently answers to.</summary>
public sealed record LiveStash(AutoStashRecord Record, string Ref);

/// <summary>What is waiting on the branch just arrived at.</summary>
/// <remarks>
/// <see cref="Stale"/> is separated from <see cref="Restorable"/> rather than filtered away,
/// because the caller has to do something with it: clear those entries and say nothing. A user
/// popping their own stash from a terminal is ordinary, and it must not read as an error.
/// </remarks>
public sealed record StashArrival(
    IReadOnlyList<LiveStash> Restorable, IReadOnlyList<AutoStashRecord> Stale)
{
    public bool HasWork => Restorable.Count > 0;
}

/// <summary>What is already in the stash list before another one is taken.</summary>
public sealed record StashSituation(int Ours, int Theirs)
{
    /// <summary>A sentence for the prompt, or empty when there is nothing worth saying.</summary>
    /// <remarks>
    /// <b>Theirs is mentioned and never touched.</b> The only reason to say it at all is that
    /// the number in <c>git stash list</c> will otherwise be a surprise — a user who reads
    /// "Versio saved 1 stash" and then sees four entries has been misled by an accurate
    /// sentence.
    /// </remarks>
    public string Describe() => (Ours, Theirs) switch
    {
        (0, 0) => string.Empty,
        (0, 1) => "There is 1 stash here that Versio did not take; it will not be touched.",
        (0, var t) => $"There are {t} stashes here that Versio did not take; " +
                      "they will not be touched.",
        (1, 0) => "You have 1 stash saved for this branch already.",
        (var o, 0) => $"You have {o} stashes saved for this branch already.",
        (1, var t) => $"You have 1 stash saved for this branch already, and {t} " +
                      "that Versio did not take and will not touch.",
        var (o, t) => $"You have {o} stashes saved for this branch already, and {t} " +
                      "that Versio did not take and will not touch.",
    };
}

/// <summary>
/// The rules the auto-stash follows, with no git and no filesystem in sight.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a decision the design document (<c>SAFEGUARDS.md</c> §4) argues for, and
/// every one of them is wrong in a way that costs somebody their work: restoring the wrong
/// stash, dropping one that was not ours, recording one that was never created, or offering
/// back one that has already been popped.
/// </para>
/// <para>
/// So it is pure, and each row of §4.6's failure table is a test.
/// </para>
/// </remarks>
public static class AutoStash
{
    /// <summary>The marker written into the stash message.</summary>
    /// <remarks>
    /// Recognisable in a terminal, and never used to identify anything: a user is free to
    /// type this exact string into their own <c>git stash push -m</c>, and identifying our own
    /// work by pattern-matching a human-readable subject is how you eventually pop somebody
    /// else's stash.
    /// </remarks>
    public static string Marker(string branch, string shortHead) =>
        $"versio-autostash {branch} @ {shortHead}";

    /// <summary>
    /// The key a stash is recorded under.
    /// </summary>
    /// <remarks>
    /// A detached HEAD has no branch name, and refusing to stash mid-switch because of that
    /// would be worse than an imperfect key: the commit is what the user can come back to, so
    /// that is what it is filed under.
    /// </remarks>
    public static string KeyFor(string? branch, string headCommit) =>
        string.IsNullOrWhiteSpace(branch)
            ? $"detached@{Short(headCommit)}"
            : branch;

    /// <summary>
    /// The stash that appeared, or null when none did.
    /// </summary>
    /// <remarks>
    /// <b>This is the correctness check, not bookkeeping.</b> <c>git stash push</c> on a clean
    /// tree prints <i>No local changes to save</i> and <b>exits 0 without creating an entry</b>
    /// — measured, not assumed — so trusting the exit code would record a stash that does not
    /// exist and then offer to restore it. The set difference proves an entry was created and
    /// yields its stable id in one read.
    /// <para>
    /// A null answer means the switch must be abandoned. A stash that did not happen and a
    /// checkout that happened anyway is the worst outcome available here.
    /// </para>
    /// </remarks>
    public static string? NewStash(
        IReadOnlyList<StashEntry> before, IReadOnlyList<StashEntry> after)
    {
        var known = before.Select(s => s.CommitId).ToHashSet(StringComparer.Ordinal);

        return after.FirstOrDefault(s => s.CommitId.Length > 0 && !known.Contains(s.CommitId))
            ?.CommitId;
    }

    /// <summary>What is waiting on the branch just arrived at.</summary>
    /// <remarks>
    /// The positional ref is resolved <b>here, at the moment of use</b>, by matching the SHA
    /// against the list git just gave us — and never stored. <c>git stash apply</c> accepts a
    /// SHA and <c>git stash drop</c> does not, so a design that stored <c>stash@{n}</c> would
    /// work in every test with one stash in the list and drop the wrong one the first time a
    /// user had two.
    /// </remarks>
    public static StashArrival OnArrival(
        string branch, AutoStashState state, IReadOnlyList<StashEntry> live)
    {
        var byId = live
            .Where(s => s.CommitId.Length > 0)
            .GroupBy(s => s.CommitId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Ref, StringComparer.Ordinal);

        var mine = state.Entries
            .Where(e => string.Equals(e.Branch, branch, StringComparison.Ordinal))
            .OrderByDescending(e => e.CreatedUtc)
            .ToList();

        return new StashArrival(
            [.. mine.Where(e => byId.ContainsKey(e.StashCommitId))
                    .Select(e => new LiveStash(e, byId[e.StashCommitId]))],
            [.. mine.Where(e => !byId.ContainsKey(e.StashCommitId))]);
    }

    /// <summary>What is already there before another stash is taken.</summary>
    public static StashSituation BeforeTaking(
        string branch, AutoStashState state, IReadOnlyList<StashEntry> live)
    {
        var ours = state.Entries
            .Select(e => e.StashCommitId)
            .ToHashSet(StringComparer.Ordinal);

        var mineHere = state.Entries.Count(
            e => string.Equals(e.Branch, branch, StringComparison.Ordinal) &&
                 live.Any(s => string.Equals(s.CommitId, e.StashCommitId, StringComparison.Ordinal)));

        return new StashSituation(mineHere, live.Count(s => !ours.Contains(s.CommitId)));
    }

    /// <summary>Records whose branch no longer exists.</summary>
    /// <remarks>
    /// <b>Never dropped automatically.</b> The stash still holds real work and the branch it
    /// belonged to is gone, which is precisely the case where somebody needs telling rather
    /// than tidying up after. Surfaced as a finding; the stash stays in git either way.
    /// </remarks>
    public static IReadOnlyList<AutoStashRecord> Orphans(
        AutoStashState state, IReadOnlyList<string> branches, IReadOnlyList<StashEntry> live)
    {
        var known = branches.ToHashSet(StringComparer.Ordinal);
        var present = live.Select(s => s.CommitId).ToHashSet(StringComparer.Ordinal);

        return
        [
            .. state.Entries.Where(
                e => present.Contains(e.StashCommitId) &&
                     !known.Contains(e.Branch) &&
                     !e.Branch.StartsWith("detached@", StringComparison.Ordinal)),
        ];
    }

    /// <summary>Add a record, newest first.</summary>
    public static AutoStashState With(AutoStashState state, AutoStashRecord record) =>
        state with { Entries = [record, .. state.Entries] };

    /// <summary>Drop records by stash id — a restore that succeeded, or a stale entry.</summary>
    public static AutoStashState Without(AutoStashState state, params string[] stashCommitIds)
    {
        var gone = stashCommitIds.ToHashSet(StringComparer.Ordinal);

        return state with
        {
            Entries = [.. state.Entries.Where(e => !gone.Contains(e.StashCommitId))],
        };
    }

    private static string Short(string commit) =>
        commit.Length >= 7 ? commit[..7] : commit;
}
