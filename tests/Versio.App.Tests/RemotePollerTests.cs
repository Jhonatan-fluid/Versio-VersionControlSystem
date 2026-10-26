using Versio.App.ViewModels;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Sync;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The loop that keeps the ahead/behind counts honest, and the four rules that keep it out of
/// the way.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RemotePoller.TickAsync"/> is driven directly rather than through the timer. A test
/// that waited for a real interval would take ten seconds to prove one thing and would be a
/// race for every other; the pacing itself is proved in <c>RemotePollPolicyTests</c> against a
/// fake clock.
/// </para>
/// <para>
/// The assertions that matter most are the negative ones — that a quiet probe fetches nothing,
/// that a busy shell probes nothing, that a refusal stops it on the first occurrence. Each is a
/// specific way this feature could make the application worse rather than better.
/// </para>
/// </remarks>
public sealed class RemotePollerTests
{
    /// <summary>A git client that answers only the two questions a probe asks.</summary>
    private sealed class ProbeClient : IGitClient
    {
        public string RemoteHead { get; set; } = "aaaaaaa";

        public string LocalHead { get; set; } = "aaaaaaa";

        /// <summary>Whether the remote answers at all. False models no connection or no
        /// credentials; true with an empty <see cref="RemoteHead"/> models a deleted branch —
        /// the two situations the probe type exists to keep apart.</summary>
        public bool Answered { get; set; } = true;

        public int LsRemoteCalls { get; private set; }

        /// <summary>Runs while the probe is in flight, so a test can model the user pressing
        /// something at exactly the wrong moment.</summary>
        public Action? DuringLsRemote { get; set; }

        public Task<RemoteBranchProbe> LsRemoteAsync(
            string remote, string branch, CancellationToken ct)
        {
            LsRemoteCalls++;
            DuringLsRemote?.Invoke();
            return Task.FromResult(Answered
                ? new RemoteBranchProbe { Answered = true, Head = RemoteHead }
                : RemoteBranchProbe.NoAnswer);
        }

        public Task<string> RevParseAsync(string revision, CancellationToken ct) =>
            Task.FromResult(LocalHead);
        public Task<string> ShowAtHeadAsync(string path, CancellationToken ct) =>
            Task.FromResult(string.Empty);


        /// <summary>What the forge's request refs come back as.</summary>
        public string RequestRefs { get; set; } = string.Empty;

        public int LsRemoteRefsCalls { get; private set; }

        public Task<string> LsRemoteRefsAsync(string remote, string pattern, CancellationToken ct)
        {
            LsRemoteRefsCalls++;
            return Task.FromResult(RequestRefs);
        }

        // Nothing else is reachable from a probe. Throwing rather than returning a default is
        // deliberate: if the poller ever starts calling something else, this says so loudly
        // instead of quietly succeeding.
        public Task<bool> IsRepositoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> VersionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GitIdentity> IdentityAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> SetIdentityAsync(string n, string e, bool g, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitStatus> StatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BranchInfo>> BranchesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> StageAsync(IReadOnlyList<string> p, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> UnstageAsync(IReadOnlyList<string> p, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> CommitAsync(string m, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> AmendCommitMessageAsync(string m, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> DiscardAsync(IReadOnlyList<string> p, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> CreateBranchAsync(string n, bool c, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> CheckoutAsync(string b, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasStoredCredentialAsync(string p, string h, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> SignInAsync(string p, string h, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> SignOutAsync(string p, string h, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> VerifyRemoteAsync(string u, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> RemoteUrlAsync(string r, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GitDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CommonGitDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> FetchAsync(IProgress<GitProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> PullAsync(IProgress<GitProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> PushAsync(IProgress<GitProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> ResolveWithSideAsync(string path, bool ours, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitResult> MergeUpstreamAsync(IProgress<GitProgress>? p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitResult> RebaseOntoUpstreamAsync(IProgress<GitProgress>? p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitResult> MergeBranchAsync(string branch, IProgress<GitProgress>? p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> IncomingPathsAsync(string branch, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitResult> AbortMergeOrRebaseAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitResult> ContinueRebaseAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MergeState> MergeStateAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> RemoteBranchesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> MergedBranchesAsync(string into, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> RemoteHeadAsync(string remote, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitResult> DeleteBranchAsync(string name, bool force, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitResult> DeleteRemoteBranchAsync(string remote, string name, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> RemotesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> PublishBranchAsync(string r, string b, IProgress<GitProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListWorkingTreeAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyDictionary<string, long>?> IndexBlobSizesAsync(IReadOnlyList<string> p, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> DiffAsync(string p, bool s, CancellationToken ct, int c = 3) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> DetectRenamesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> HeadCommitAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommitEntry>> RecentCommitsAsync(int c, int s, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommitEntry>> CommitsInRangeAsync(string r, int c, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommitFile>> RangeFilesAsync(string r, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> UnpushedCommitIdsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CommitPatchAsync(string c, CancellationToken ct, int l = 3) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommitFile>> CommitFilesAsync(string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CommitFileDiffAsync(string c, string p, CancellationToken ct, int l = 3) => throw new NotSupportedException();
        public Task<string?> CommitMessageAsync(string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<StashEntry>> StashListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> StashPushAsync(string? m, bool u, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> StashApplyAsync(string r, bool d, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> StashDropAsync(string r, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResult> ApplyToIndexAsync(string p, bool r, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsLfsAvailableAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Harness
    {
        public ProbeClient Git { get; } = new();

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));

        public bool Suspended { get; set; }

        public int Fetches { get; private set; }

        public List<string> Halts { get; } = [];

        public InMemoryLogSink Sink { get; } = new();

        public RemotePoller Poller { get; }

        public Harness(bool hasTarget = true)
        {
            var logger = new VersioLogger(Clock).AddSink(Sink);

            Poller = new RemotePoller(
                logger,
                Clock,
                client: () => Git,
                suspended: () => Suspended,
                target: () => hasTarget ? ("origin", "main") : null,
                onMoved: _ =>
                {
                    Fetches++;
                    return Task.CompletedTask;
                },
                onHalted: Halts.Add,
                onProbed: _ =>
                {
                    SecondQuestions++;
                    return Task.CompletedTask;
                });
        }

        /// <summary>How many times the loop asked its caller's extra question.</summary>
        public int SecondQuestions { get; private set; }

        public Task TickAsync() => Poller.TickAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_probe_that_finds_nothing_fetches_nothing()
    {
        var h = new Harness();

        await h.TickAsync();

        // The steady state, and the whole reason the frequent call is ls-remote rather than a
        // fetch: one small round trip, no object transfer, nothing changed.
        Assert.Equal(1, h.Git.LsRemoteCalls);
        Assert.Equal(0, h.Fetches);
    }

    [Fact]
    public async Task The_callers_second_question_is_asked_even_when_nothing_moved()
    {
        // THE POINT OF onProbed. Opening a pull request does not move the branch head — the
        // ref the forge publishes points at a commit that already exists — so a question
        // conditioned on movement would never notice one being opened, and the button would
        // sit on "Create pull request" for a branch that already has one.
        var h = new Harness();

        await h.TickAsync();

        Assert.Equal(0, h.Fetches);
        Assert.Equal(1, h.SecondQuestions);
    }

    [Fact]
    public async Task The_second_question_is_not_asked_when_the_remote_did_not_answer()
    {
        // No answer means no connection or no credentials. Asking a second question down the
        // same pipe is a second timeout for the same information.
        var h = new Harness();
        h.Git.Answered = false;

        await h.TickAsync();

        Assert.Equal(0, h.SecondQuestions);
    }

    [Fact]
    public async Task The_second_question_is_suspended_with_everything_else()
    {
        var h = new Harness { Suspended = true };

        await h.TickAsync();

        Assert.Equal(0, h.SecondQuestions);
    }

    [Fact]
    public async Task A_moved_remote_is_fetched_exactly_once()
    {
        var h = new Harness();
        h.Git.RemoteHead = "bbbbbbb";

        await h.TickAsync();

        Assert.Equal(1, h.Fetches);
    }

    [Fact]
    public async Task A_branch_gone_from_an_answering_remote_is_not_a_failure()
    {
        // THE MISCLASSIFICATION (FINDINGS 2026-08-23). A forge deleting the branch after a
        // merge made ls-remote answer with nothing, which used to be recorded as Failed -
        // feeding the circuit breaker until the watch halted "unreachable" over a remote
        // that was answering perfectly well.
        var h = new Harness();
        h.Git.RemoteHead = string.Empty;

        for (var i = 0; i < RemotePollPolicy.FailuresBeforeHalt + 1; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await h.TickAsync();
        }

        Assert.False(h.Poller.Policy.Halted);
        Assert.Empty(h.Halts);
    }

    [Fact]
    public async Task A_gone_branch_with_a_stale_tracking_ref_triggers_the_pruning_fetch()
    {
        // The one moment there is something to learn: the local ref still exists, so
        // porcelain is still counting against a ghost and the badge says "up to date". The
        // fetch prunes the ref; the refresh that follows reports the upstream as gone.
        var h = new Harness();
        h.Git.RemoteHead = string.Empty;
        h.Git.LocalHead = "aaaaaaa";

        await h.TickAsync();

        Assert.Equal(1, h.Fetches);

        var line = h.Sink.Snapshot()
                    .First(e => e.Level == LogLevel.Information && e.Message.Contains("gone"));
        Assert.Contains("origin/main", line.Message);
    }

    [Fact]
    public async Task A_gone_branch_already_pruned_is_quiet_rather_than_fetched_again()
    {
        // After the prune the poller keeps watching - the branch could be recreated - but
        // every further probe finds the same absence. Fetching on each would be a network
        // call every interval to learn nothing, the exact thing the probe design exists to
        // avoid.
        var h = new Harness();
        h.Git.RemoteHead = string.Empty;
        h.Git.LocalHead = string.Empty;

        for (var i = 0; i < 3; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await h.TickAsync();
        }

        Assert.Equal(0, h.Fetches);
        Assert.False(h.Poller.Policy.Halted);
    }

    [Fact]
    public async Task A_gone_branch_recreated_on_the_remote_is_fetched()
    {
        // The way back: pushing the branch again (or a teammate doing so) makes the probe
        // see a head where the pruned local ref has none, which is ordinary movement.
        var h = new Harness();
        h.Git.RemoteHead = "ccccccc";
        h.Git.LocalHead = string.Empty;

        await h.TickAsync();

        Assert.Equal(1, h.Fetches);
    }

    [Fact]
    public async Task Nothing_happens_while_the_shell_is_busy()
    {
        var h = new Harness { Suspended = true };

        await h.TickAsync();

        // A probe competing for the network and the credential helper during a push is pure
        // noise, and one that landed mid-push would be worse than noise.
        Assert.Equal(0, h.Git.LsRemoteCalls);
        Assert.Equal(0, h.Fetches);
    }

    [Fact]
    public async Task A_result_arriving_after_something_starts_is_dropped()
    {
        var h = new Harness();
        h.Git.RemoteHead = "bbbbbbb";

        // The user presses Push while the probe is already in flight. The poller checks the
        // flag a second time before acting, so its fetch does not land in the middle of theirs.
        h.Git.DuringLsRemote = () => h.Suspended = true;

        await h.TickAsync();

        Assert.Equal(1, h.Git.LsRemoteCalls);
        Assert.Equal(0, h.Fetches);
    }

    [Fact]
    public async Task Nothing_is_probed_without_an_upstream_to_compare_against()
    {
        var h = new Harness(hasTarget: false);

        await h.TickAsync();

        // A branch never published has no remote counterpart, so asking would fail on every
        // probe and log a failure every interval for as long as the branch existed.
        Assert.Equal(0, h.Git.LsRemoteCalls);
    }

    [Fact]
    public async Task The_interval_is_respected_between_probes()
    {
        var h = new Harness();

        await h.TickAsync();
        await h.TickAsync();
        await h.TickAsync();

        // Three turns of the loop, one probe: the loop ticks and the policy decides.
        Assert.Equal(1, h.Git.LsRemoteCalls);

        h.Clock.Advance(RemotePollPolicy.DefaultInterval + TimeSpan.FromSeconds(1));
        await h.TickAsync();

        Assert.Equal(2, h.Git.LsRemoteCalls);
    }

    [Fact]
    public async Task A_refusal_stops_it_on_the_first_one()
    {
        var h = new Harness();

        h.Poller.NoteRefused("Authentication failed");

        Assert.True(h.Poller.Policy.Halted);
        Assert.Single(h.Halts);

        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.TickAsync();

        // Retrying a rejected credential every ten seconds cannot succeed and may lock an
        // account. An hour of elapsed time must not restart it.
        Assert.Equal(0, h.Git.LsRemoteCalls);
    }

    [Fact]
    public void A_halt_is_announced_once_rather_than_on_every_tick()
    {
        var h = new Harness();

        h.Poller.NoteRefused("Authentication failed");
        h.Poller.NoteRefused("Authentication failed");
        h.Poller.NoteRefused("Authentication failed");

        // One warning card, not a wall of them.
        Assert.Single(h.Halts);
    }

    [Fact]
    public async Task Resuming_starts_it_again()
    {
        var h = new Harness();
        h.Poller.NoteRefused("Authentication failed");

        h.Poller.Resume();
        await h.TickAsync();

        Assert.Equal(1, h.Git.LsRemoteCalls);
    }

    [Fact]
    public async Task A_quiet_run_is_summarised_rather_than_logged_line_by_line()
    {
        var h = new Harness();

        for (var i = 0; i < 30; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await h.TickAsync();
        }

        var info = h.Sink.Snapshot()
                    .Where(e => e.Level == LogLevel.Information)
                    .Select(e => e.Message)
                    .ToList();

        // Every probe is logged at Debug, always. What must NOT happen is an Information line
        // per probe: at this rate that is how the log panel got drowned before.
        Assert.True(info.Count < 30, $"{info.Count} Information lines for 30 probes");

        var debug = h.Sink.Snapshot().Count(e => e.Level == LogLevel.Debug);
        Assert.True(debug >= 30, "every probe should still leave a Debug line");
    }

    [Fact]
    public async Task Movement_is_logged_at_information_with_both_commits()
    {
        var h = new Harness();
        h.Git.LocalHead = "1111111aaaa";
        h.Git.RemoteHead = "2222222bbbb";

        await h.TickAsync();

        var line = h.Sink.Snapshot()
                    .First(e => e.Level == LogLevel.Information && e.Message.Contains("moved"));

        // The line somebody looks for when asking "when did the remote change".
        Assert.Contains("1111111", line.Message);
        Assert.Contains("2222222", line.Message);
    }
}
