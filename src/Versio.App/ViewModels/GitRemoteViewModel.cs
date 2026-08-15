using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Logging;

namespace Versio.App.ViewModels;

/// <summary>
/// Where this branch stands against the remote, and everything that moves it.
/// </summary>
/// <remarks>
/// <para>
/// The second collaborator out of <c>ShellViewModel</c>, and it was chosen over staging on
/// evidence rather than on the order the queue listed. <c>GIT-PANEL-PLAN.md</c>'s GP-E log had
/// already measured the staging seam and rejected it — <i>"a move dressed as a decomposition"</i>,
/// because <c>CommitAsync</c> alone reaches eight collaborators — and named this surface as the
/// one worth cutting instead. Re-measured before starting: staging makes <b>four</b> calls into
/// the PLC engine including <c>EvaluateGateAsync</c> and <c>SatisfyGateAsync</c>, so its seam
/// runs straight through the junction where git meets the toolchain. This one makes <b>two</b>,
/// and both are narrow.
/// </para>
/// <para>
/// <b>It is heavier than <see cref="GitAuthViewModel"/> and that is worth stating plainly.</b>
/// Auth borrowed four things. This borrows nine, and the reason is the poller: a background
/// watch is defined by its callbacks, and those callbacks belong to the shell — what counts as
/// busy, what to do when the remote moves, what to ask on each probe. They were already
/// delegates before this moved, which is what makes the count honest rather than alarming; the
/// alternative is passing the shell itself, and that is the anti-pattern GP-E named.
/// </para>
/// <para>
/// <b><see cref="ShellViewModel"/> keeps <c>ReconcileAsync</c>.</b> Exiting a divergence reads
/// <c>Conflicted</c> and <c>IsMidMerge</c> and belongs with the conflict surface from GP-G, not
/// here. Including it raised the borrow count from nine to eleven for one method, which is the
/// measurement that decided it.
/// </para>
/// <para>
/// <b>It NEVER pulls in the background.</b> The poller probes with <c>ls-remote</c> and fetches
/// only when the remote has actually moved. A pull rewrites the project and its mirror together,
/// under the watcher — the one scenario <c>SAFEGUARDS.md</c> §5.A calls able to corrupt a
/// project through no user error.
/// </para>
/// </remarks>
public sealed class GitRemoteViewModel : ObservableObject, IDisposable
{
    private readonly VersioLogger _logger;
    private readonly BusyTracker _busy;
    private readonly NotificationCenter _notifications;
    private readonly GitAuthViewModel _auth;
    private readonly SyncViewModel _sync;
    private readonly Func<IGitClient?> _client;
    private readonly Func<string> _branchName;
    private readonly Func<GuardIntent, string, Task<bool>> _clearForContextChange;
    private readonly Func<RefreshTrigger, Task> _refresh;
    private readonly RemotePoller _poller;

    private RemoteStanding _standing = RemoteStanding.None;
    private bool _hasRemote;
    private string _aheadBehind = string.Empty;

    /// <param name="client">
    /// Read on each use rather than captured, because the shell replaces it every time a
    /// repository opens and a captured null would outlive the first open.
    /// </param>
    /// <param name="ready">The shell's own gate: a client, and nothing running.</param>
    /// <param name="suspended">
    /// One shared flag, so a probe cannot compete with a push for the network or the credential
    /// helper, and a modal question is never answered by a background fetch.
    /// </param>
    /// <param name="clearForContextChange">
    /// The guard every context change goes through. Pull is the one operation here that can
    /// meet a dirty tree, and the worst one to meet it on.
    /// </param>
    /// <param name="onProbed">
    /// Asked on the poller's schedule rather than a timer of its own, so it inherits the
    /// backoff, the suspension and the halt on a rejected credential.
    /// </param>
    public GitRemoteViewModel(
        VersioLogger logger,
        IClock clock,
        BusyTracker busy,
        NotificationCenter notifications,
        GitAuthViewModel auth,
        SyncViewModel sync,
        Func<IGitClient?> client,
        Func<bool> ready,
        Func<bool> suspended,
        Func<string> branchName,
        Func<GuardIntent, string, Task<bool>> clearForContextChange,
        Func<RefreshTrigger, Task> refresh,
        Func<CancellationToken, Task> onProbed,
        Action<Exception> reportError)
    {
        _logger = logger;
        _busy = busy;
        _notifications = notifications;
        _auth = auth;
        _sync = sync;
        _client = client;
        _branchName = branchName;
        _clearForContextChange = clearForContextChange;
        _refresh = refresh;

        // Keeps the ahead/behind counts honest without anybody asking. It probes with
        // ls-remote and fetches only when the remote has actually moved - and it NEVER pulls,
        // because a pull rewrites the project and its mirror under the watcher, which is the
        // one scenario SAFEGUARDS §5.A calls able to corrupt a project through no user error.
        _poller = new RemotePoller(
            logger,
            clock,
            client: client,
            suspended: suspended,
            target: PollTarget,
            onMoved: FetchBecauseTheRemoteMovedAsync,
            onHalted: reason => OnUi(() => notifications.Warning(
                "Not watching the remote any more.", reason)),
            onProbed: onProbed);

        // Pull is enabled whenever an upstream exists, NOT only when behind. Behind-ness is
        // as fresh as the last fetch, and disabling a safe fast-forward on stale knowledge
        // would leave somebody unable to act on a remote that moved a second ago.
        PullCommand = new AsyncCommand(
            PullAsync, () => ready() && Standing.CanPull, reportError);

        // Push stays live while diverged, where git will reject it. Disabling it would hide
        // the state the user has to resolve; git's own rejection, in a notification that does
        // not expire, is the clearest possible explanation of what is wrong.
        PushCommand = new AsyncCommand(() => PushAsync(afterCommit: false),
                                       () => ready() && Standing.CanPush, reportError);

        FetchCommand = new AsyncCommand(FetchAsync, ready, reportError);

        PublishCommand = new AsyncCommand(
            PublishAsync, () => ready() && Standing.CanPublish, reportError);
    }

    /// <summary>
    /// The standing moved, and the shell's own state depends on it.
    /// </summary>
    /// <remarks>
    /// The same seam <see cref="GitAuthViewModel.Changed"/> is, for the same reason: this used
    /// to call <c>RaiseHealth</c> and <c>RaiseGitCommandStates</c> directly, and could not have
    /// moved while it did. Deleting the auth subscription left the whole suite green, so this
    /// one ships with a test that fails without it.
    /// </remarks>
    public event Action? Changed;

    /// <summary>
    /// Where this branch stands in relation to the remote, in words.
    /// </summary>
    /// <remarks>
    /// Replaces reading the two arrows. <see cref="AheadBehind"/> survives for the branch
    /// dropdown, where a compact glyph beside a name is the right shape; the panel itself now
    /// says what the numbers mean, because that is the question people open it to answer.
    /// </remarks>
    public RemoteStanding Standing
    {
        get => _standing;
        private set
        {
            if (SetProperty(ref _standing, value))
            {
                OnPropertyChanged(nameof(PushLabel));
                OnPropertyChanged(nameof(PullLabel));
                OnPropertyChanged(nameof(ShowPublish));
                OnPropertyChanged(nameof(ShowPush));
                Changed?.Invoke();
            }
        }
    }

    public string AheadBehind
    {
        get => _aheadBehind;
        private set => SetProperty(ref _aheadBehind, value);
    }

    /// <summary>Push and Publish swap places: they are different operations, so only one is
    /// offered at a time rather than one button meaning two things.</summary>
    public bool ShowPublish => _standing.CanPublish;

    public bool ShowPush => !_standing.CanPublish;

    /// <summary>The count on the label, where it informs without gating.</summary>
    public string PushLabel => "Push" + _standing.PushCountSuffix;

    public string PullLabel => "Pull" + _standing.PullCountSuffix;

    public AsyncCommand PullCommand { get; }

    public AsyncCommand PushCommand { get; }

    public AsyncCommand FetchCommand { get; }

    public AsyncCommand PublishCommand { get; }

    /// <summary>The remote watch, so a test can assert what this shell wired into it.</summary>
    /// <remarks>
    /// Internal for the same reason <c>IgnoredPaths()</c> is: trusting that a callback written
    /// in this file reaches an object constructed in this file is exactly the assumption that
    /// has been wrong three times here.
    /// </remarks>
    internal RemotePoller Poller => _poller;

    // --------------------------------------------------------------- the shell's hooks

    /// <summary>Whether this repository has anywhere to push at all.</summary>
    /// <remarks>
    /// Read once per repository rather than per refresh: remotes are configuration, they change
    /// when somebody edits them, and a process spawn on every working-tree save to re-learn that
    /// origin still exists is waste. Set before the first refresh, so the very first
    /// <see cref="Standing"/> computed is already right — otherwise a repository with a remote
    /// flashes "no remote configured" for one frame on every open.
    /// </remarks>
    public void NoteHasRemote(bool hasRemote) => _hasRemote = hasRemote;

    /// <summary>Whether this repository has a remote configured at all.</summary>
    /// <remarks>
    /// Read by the shell when it resolves the default branch: with no remote there is no
    /// <c>origin/HEAD</c> to ask about, and asking costs a process spawn to be told so.
    /// </remarks>
    public bool HasRemote => _hasRemote;

    /// <summary>Recompute the standing from a status git has just reported.</summary>
    public void NoteStatus(BranchInfo branch)
    {
        // The remote list is read once per repository rather than per refresh: remotes are
        // configuration, they change when somebody edits them, and a process spawn on every
        // working-tree save to re-learn that origin still exists is waste.
        AheadBehind = branch is { Ahead: 0, Behind: 0 }
            ? string.Empty
            : $"↑{branch.Ahead} ↓{branch.Behind}";

        Standing = RemoteStanding.For(branch, _hasRemote);
    }

    /// <summary>Start watching the remote. Idempotent.</summary>
    /// <remarks>
    /// Only once there is something to watch: <see cref="PollTarget"/> returns nothing until a
    /// standing with an upstream has been computed.
    /// </remarks>
    public void StartWatching() => _poller.Start();

    /// <summary>Forget the repository being left.</summary>
    /// <remarks>
    /// Resume clears the halt as well, so a credential problem on the last repository does not
    /// silence the next.
    /// </remarks>
    public void Reset()
    {
        AheadBehind = string.Empty;
        Standing = RemoteStanding.None;
        _hasRemote = false;

        _poller.Stop();
        _poller.Resume();
    }

    /// <summary>
    /// Ask the watch to look now.
    /// </summary>
    /// <remarks>
    /// The backoff exists to stop polling for nobody. Any deliberate git action, or opening the
    /// panel, means there is a somebody — and the counts should be as live as they were at the
    /// start rather than on a sixty-second cadence inherited from an idle hour.
    /// </remarks>
    public void Wake() => _poller.Wake();

    /// <summary>Re-ask every command in here whether it can run.</summary>
    /// <remarks>Called by the shell's own sweep: these are gated on its busy flag.</remarks>
    public void RaiseCommandStates()
    {
        PullCommand.RaiseCanExecuteChanged();
        PushCommand.RaiseCanExecuteChanged();
        FetchCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Push, from the commit flow rather than from the button.</summary>
    public Task PushAfterCommitAsync() => PushAsync(afterCommit: true);

    internal Task PullForTestAsync() => PullAsync();

    // ------------------------------------------------------------------- the operations

    private async Task PullAsync()
    {
        if (_client() is not { } git)
        {
            return;
        }

        _logger.Click("Git.Pull");

        // Guarded at last. GuardIntent.Pull has existed, been tested and been deliberately
        // unwired since the guard was written, because every verdict it produces resolves to
        // the auto-stash - and offering to stash before anything could would have been a
        // button that lies. A pull is the last path that can meet a dirty tree, and it is the
        // worst one to meet it on: it rewrites the project and the mirror together.
        if (!await _clearForContextChange(GuardIntent.Pull, "pulling").ConfigureAwait(true))
        {
            return;
        }

        using var busy = _busy.Begin(WorkKind.GitWrite, "Pulling…");

        // Same as a checkout: a pull can land a whole new mirror, and an unheld watcher would
        // report it as an edit to the exported text and import it into the project.
        GitResult result = default!;

        await _sync.WhileGitRewritesTheTreeAsync(
            "pull",
            async () => result = await git.PullAsync(null, busy.Token).ConfigureAwait(true))
            .ConfigureAwait(true);

        if (result.Success)
        {
            _auth.NoteAuthSuccess();
            _notifications.Success("Pulled.");
        }
        else
        {
            _auth.NoteAuthFailure(result);
            _notifications.Failure("Pull failed.", result.FailureReason ?? string.Empty);
        }

        await _refresh(RefreshTrigger.Manual).ConfigureAwait(true);
    }

    /// <summary>
    /// Push, and release the direction locks if it worked.
    /// </summary>
    /// <remarks>
    /// Push is what releases the lock, not commit: until work is pushed nobody else can see
    /// it, so the direction is still meaningfully owned (plan section 6.5). Push is repo-wide,
    /// so it releases every binding rather than the one last looked at.
    /// </remarks>
    private async Task PushAsync(bool afterCommit)
    {
        if (_client() is not { } git)
        {
            return;
        }

        if (!afterCommit)
        {
            _logger.Click("Git.Push");
        }

        using var busy = _busy.Begin(WorkKind.GitPush, "Pushing…");
        var result = await git.PushAsync(null, busy.Token).ConfigureAwait(true);

        if (!result.Success)
        {
            // Sticky, and that is the single most important lifetime rule in the panel. A
            // failed push that quietly vanished after four seconds is how somebody goes home
            // believing their work is on the remote.
            _auth.NoteAuthFailure(result);
            _notifications.Failure("Push failed.", result.FailureReason ?? string.Empty);
            return;
        }

        var head = await git.HeadCommitAsync(busy.Token).ConfigureAwait(true);
        await _sync.ReleaseAfterPushAsync(head, busy.Token).ConfigureAwait(true);

        _auth.NoteAuthSuccess();

        // Posted BEFORE the refresh, and it survives it. That ordering used to be the bug:
        // StatusSummary was set here and RunRefreshAsync then overwrote it with the file
        // count, so a successful push said nothing at all.
        _notifications.Success("Pushed.", "Every direction lock is released.");
        await _refresh(RefreshTrigger.Manual).ConfigureAwait(true);
    }

    /// <summary>
    /// Put this branch on the remote for the first time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The remote is chosen, never guessed silently.</b> <c>origin</c> when it exists — which
    /// it does for anything that was cloned — then the sole remote if there is exactly one.
    /// With several and no <c>origin</c>, this refuses and says so rather than picking: pushing
    /// a branch to the wrong remote is not something to do on a coin toss, and a repository
    /// with three remotes is one where somebody has already thought about which is which.
    /// </para>
    /// <para>
    /// Releases the direction locks on success exactly as <see cref="PushAsync"/> does. The
    /// lock rule is about work becoming visible to other people, and a publish makes it visible
    /// for the first time — if anything it is the more complete case.
    /// </para>
    /// </remarks>
    private async Task PublishAsync()
    {
        if (_client() is not { } git)
        {
            return;
        }

        _logger.Click("Git.Publish");
        using var scope = _logger.BeginOperation(LoggerExtensions.NewOperationId());

        var remotes = await git.RemotesAsync(CancellationToken.None).ConfigureAwait(true);

        var remote = remotes.FirstOrDefault(r => r == "origin")
                     ?? (remotes.Count == 1 ? remotes[0] : null);

        if (remote is null)
        {
            _notifications.Failure(
                "Versio does not know which remote to publish to.",
                remotes.Count == 0
                    ? "This repository has no remote."
                    : $"It has {remotes.Count} remotes and none is called origin: " +
                      string.Join(", ", remotes));
            return;
        }

        var branch = _branchName();

        using var busy = _busy.Begin(WorkKind.GitPush, $"Publishing {branch} to {remote}…");
        var result = await git.PublishBranchAsync(remote, branch, null, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            _auth.NoteAuthFailure(result);
            _notifications.Failure($"Could not publish {branch}.",
                                   result.FailureReason ?? string.Empty);
            return;
        }

        var head = await git.HeadCommitAsync(busy.Token).ConfigureAwait(true);
        await _sync.ReleaseAfterPushAsync(head, busy.Token).ConfigureAwait(true);

        _auth.NoteAuthSuccess();
        _notifications.Success($"{branch} is on {remote} now.",
                               "Every direction lock is released.");

        await _refresh(RefreshTrigger.GitMetadata).ConfigureAwait(true);
    }

    private async Task FetchAsync()
    {
        if (_client() is not { } git)
        {
            return;
        }

        _logger.Click("Git.Fetch");
        using var busy = _busy.Begin(WorkKind.GitRead, "Fetching…");

        var result = await git.FetchAsync(null, busy.Token).ConfigureAwait(true);

        if (result.Success)
        {
            _auth.NoteAuthSuccess();
            _notifications.Success("Fetched.");
        }
        else
        {
            _auth.NoteAuthFailure(result);
            _notifications.Failure("Fetch failed.", result.FailureReason ?? string.Empty);
        }

        await _refresh(RefreshTrigger.Manual).ConfigureAwait(true);
    }

    /// <summary>Which remote and branch the poller should watch, or nothing.</summary>
    /// <remarks>
    /// Nothing unless there is an upstream: a branch that has never been published has no
    /// remote counterpart to compare against, and asking about one would fail on every probe.
    /// </remarks>
    private (string Remote, string Branch)? PollTarget()
    {
        if (_client() is null || !_standing.HasUpstream)
        {
            return null;
        }

        // "origin/main" -> ("origin", "main"). The upstream is stored in exactly that shape by
        // porcelain v2, and a branch name may itself contain slashes, so only the first is a
        // separator.
        var slash = _standing.Upstream.IndexOf('/');
        return slash <= 0
            ? null
            : (_standing.Upstream[..slash], _standing.Upstream[(slash + 1)..]);
    }

    /// <summary>
    /// The remote moved, so learn what it moved to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fetch, never a pull. This is the only expensive call the poller makes and it runs only
    /// when a probe has already established there is something new.
    /// </para>
    /// <para>
    /// <b>It does not raise the <see cref="BusyTracker"/>.</b> Every git button consults that
    /// flag, so raising it here would make the panel flicker dead whenever a colleague pushed —
    /// the same reasoning that keeps automatic status refreshes off the progress bar.
    /// </para>
    /// </remarks>
    private async Task FetchBecauseTheRemoteMovedAsync(CancellationToken ct)
    {
        if (_client() is not { } git)
        {
            return;
        }

        var result = await git.FetchAsync(null, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            // Stops on the first refusal rather than after the usual run: retrying a
            // rejected credential every few seconds cannot succeed and may lock an account.
            // The classification, and the hop back to the UI thread it needs, belong to the
            // credential collaborator; what stays here is the poller.
            if (_auth.NoteBackgroundFailure(result) is { } refusal)
            {
                _poller.NoteRefused(refusal);
            }

            return;
        }

        // Back onto the UI thread before anything observable moves. Watcher and timer callbacks
        // arrive on threadpool threads, and touching a bound collection from one throws.
        OnUi(() => _ = _refresh(RefreshTrigger.GitMetadata));
    }

    /// <summary>Stop the background loop.</summary>
    /// <remarks>
    /// A loop outliving the window would keep spawning git processes against a repository
    /// nobody is looking at. The shell owns this object and disposes it, the way it owns and
    /// disposes the log console.
    /// </remarks>
    public void Dispose() => _poller.Dispose();
}
