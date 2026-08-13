using System.Globalization;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Plc;
using Versio.Core.Locking;
using Versio.Core.Hashing;
using Versio.Core.Logging;
using Versio.Core.Watching;

namespace Versio.Core.Sync;

/// <summary>The tuning knobs behind the loop defences. The defaults are the shipped
/// values; tests override them to make timing behaviour observable without waiting.</summary>
public sealed record OrchestratorOptions
{
    /// <summary>Import is detected automatically but applied only on confirmation, because
    /// writing into a .project is the strictly more dangerous direction.</summary>
    public bool RequireImportConfirmation { get; init; } = true;

    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int CircuitBreakerThreshold { get; init; } = 5;
    public TimeSpan CircuitBreakerWindow { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>One toolchain run, announced as it starts and again as it ends.</summary>
/// <remarks>
/// <para>
/// Carries the <see cref="OperationId"/> because that is what makes the announcement useful:
/// it is the same id the runner tags its progress with and the same id every log line for
/// this run carries, so a caller can start a heartbeat keyed to it and have the two meet.
/// </para>
/// <para>
/// This exists because the UI could not tell the difference between "nothing is happening"
/// and "CODESYS has been running for twenty seconds". Wrapping the call to
/// <see cref="SyncOrchestrator.HandleChangeAsync"/> is not the same thing: the overwhelming
/// majority of calls are refused by a gate in milliseconds — a save that changed nothing, a
/// coalesced burst, a throttled repeat — and a bar that flashed for each of those would be
/// worse than none.
/// </para>
/// </remarks>
public readonly record struct SyncRunNotice(
    string BindingId, PlcOperation Operation, string OperationId);

/// <summary>
/// Reconciles a CODESYS project against its exported text.
/// </summary>
/// <remarks>
/// <para>
/// This is where the three loop defences, the direction lock, the coalescer and the circuit
/// breaker meet. It is deliberately built on abstractions only — no filesystem, no
/// processes, no clock — so every dangerous path can be exercised in a unit test: crash
/// mid-export, reboot with unsynced work, a save storm, and a genuine attempt to loop.
/// </para>
/// <para>
/// <b>Order of checks matters and is not arbitrary.</b> The circuit breaker comes first
/// because a tripped breaker means we no longer trust our own reasoning. The hash gate
/// comes before the lock because an event carrying no actual change should be silently
/// dropped rather than reported as a lock violation — otherwise a no-op save would show the
/// user a scary refusal.
/// </para>
/// <para>
/// <b>Every decision is logged, and refusals especially.</b> A refusal is invisible by
/// design — the user saves, and nothing happens — so an unlogged one leaves them with no
/// way to find out why. Every refusal is therefore built by <see cref="Refuse"/> rather
/// than by calling <see cref="SyncOutcome.Refused"/> directly, so a path added later cannot
/// quietly be a silent one.
/// </para>
/// </remarks>
public sealed class SyncOrchestrator
{
    private readonly IClock _clock;
    private readonly IPlcRunner _toolchain;
    private readonly IFileSystem _fs;
    private readonly ILockStore _lockStore;
    private readonly OrchestratorOptions _options;
    private readonly string _repoRoot;
    private readonly LockOwner _owner;
    private readonly IProgress<PlcProgress>? _progress;

    /// <summary>How many project backups to keep per binding before an import.</summary>
    private const int BackupsKept = 10;

    private readonly Dictionary<string, ProjectBinding> _bindings;
    private readonly Dictionary<string, BindingLock> _locks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobCoalescer> _coalescers = new(StringComparer.Ordinal);

    /// <summary>
    /// The job the throttle turned away, per binding, waiting for its interval to pass.
    /// </summary>
    /// <remarks>
    /// One per binding, overwritten rather than queued: the coalescer's whole premise is that
    /// an export reads the current file, so the newest deferred job describes every older one.
    /// </remarks>
    private readonly Dictionary<string, SyncJob> _deferred = new(StringComparer.Ordinal);
    private readonly CircuitBreaker _breaker;
    private readonly SelfWriteSuppressor _suppressor;
    private readonly IVersioLogger _log;

    /// <param name="logger">Optional so the several hundred unit tests that construct this
    /// need not mention it; it defaults to <see cref="NullLogger.Instance"/> rather than to
    /// null, so no call site below has to check.</param>
    /// <summary>
    /// Asks whether a vendor IDE has the project open. Optional: without one, every project
    /// reads as free and the behaviour is exactly what it was before this existed.
    /// </summary>
    private readonly IProjectOccupancyProbe? _occupancy;

    /// <summary>
    /// Asks whether the working tree is mid-merge. Defaults to "never", which is what the
    /// engine assumed before anybody asked.
    /// </summary>
    private readonly IConflictProbe _conflicts;

    public SyncOrchestrator(
        string repoRoot,
        IReadOnlyList<ProjectBinding> bindings,
        LockOwner owner,
        IClock clock,
        IFileSystem fs,
        IPlcRunner toolchain,
        ILockStore lockStore,
        OrchestratorOptions? options = null,
        IProgress<PlcProgress>? progress = null,
        IVersioLogger? logger = null,
        IProjectOccupancyProbe? occupancy = null,
        IConflictProbe? conflicts = null)
    {
        _occupancy = occupancy;
        _conflicts = conflicts ?? NoConflicts.Instance;
        _repoRoot = repoRoot;
        _owner = owner;
        _progress = progress;
        _clock = clock;
        _fs = fs;
        _toolchain = toolchain;
        _lockStore = lockStore;
        _log = logger ?? NullLogger.Instance;
        _options = options ?? new OrchestratorOptions();
        _bindings = bindings.ToDictionary(b => b.Id, StringComparer.Ordinal);
        _breaker = new CircuitBreaker(clock, _options.CircuitBreakerThreshold,
                                      _options.CircuitBreakerWindow);
        _suppressor = new SelfWriteSuppressor(clock);

        foreach (var binding in bindings)
        {
            _locks[binding.Id] = new BindingLock { Direction = SyncDirection.Clean };
            _coalescers[binding.Id] =
                new JobCoalescer(clock, _options.MinimumInterval);
        }

        _log.Info(LogCategory.Sync, bindings.Count == 0
            ? $"orchestrator ready for {_repoRoot}; no projects are tracked yet"
            : $"orchestrator ready for {_repoRoot}; tracking " +
              string.Join(", ", bindings.Select(b => $"{b.Id} ({b.ProjectPath})")));

        // The thresholds decide whether a save is throttled, coalesced or refused outright,
        // and they are the first thing to check when the answer to "why did nothing happen"
        // turns out to be "it did, thirty seconds ago".
        _log.Debug(LogCategory.Sync,
            $"options: importConfirmation={_options.RequireImportConfirmation} " +
            $"minInterval={_options.MinimumInterval.TotalSeconds:F0}s " +
            $"breaker={_options.CircuitBreakerThreshold}/" +
            $"{_options.CircuitBreakerWindow.TotalSeconds:F0}s");
    }

    public SyncDirection StateOf(string bindingId) =>
        _locks.TryGetValue(bindingId, out var l) ? l.Direction : SyncDirection.Clean;

    /// <summary>
    /// Why a binding is in the state it is in, in the words recorded when it got there.
    /// </summary>
    /// <remarks>
    /// Read from the persisted lock record rather than from memory, so it survives a restart:
    /// a project that failed to export yesterday evening still explains itself this morning,
    /// which is precisely when somebody opens the app and asks what happened. Empty when
    /// nothing was recorded, which the caller must treat as "no explanation" rather than
    /// inventing one.
    /// </remarks>
    public string ReasonFor(string bindingId) =>
        _locks.TryGetValue(bindingId, out var l) ? l.Reason : string.Empty;

    public bool AnyLockHeld => _locks.Values.Any(l => SyncStateMachine.HoldsLock(l.Direction));

    public CircuitBreaker Breaker => _breaker;

    /// <summary>
    /// A toolchain run has passed every gate and is starting now.
    /// </summary>
    /// <remarks>
    /// Raised on whatever thread the change arrived on, which for the automatic path is a
    /// watcher's timer thread. Marshalling to a UI thread is the subscriber's job — the same
    /// contract the watchers already use.
    /// </remarks>
    public event Action<SyncRunNotice>? RunStarted;

    /// <summary>That run has finished, whether it succeeded, failed or threw.</summary>
    /// <remarks>
    /// Raised from a <c>finally</c>, so a subscriber that turns something on in
    /// <see cref="RunStarted"/> cannot be left with it on. That matters more than it looks:
    /// the thing being turned on is a progress bar, and a progress bar that never goes away
    /// is indistinguishable from a hang.
    /// </remarks>
    public event Action<SyncRunNotice>? RunFinished;

    /// <summary>
    /// Restore state written by a previous session.
    /// </summary>
    /// <remarks>
    /// An abandoned lock is never simply cleared: the unsynced work it guards is still
    /// unsynced after a reboot. Recovery re-validates against content hashes, which is why
    /// it needs the current project hashes rather than trusting elapsed time.
    /// </remarks>
    public async Task<IReadOnlyList<RecoveryDecision>> RecoverAsync(
        LockRecoveryPolicy policy, CancellationToken ct)
    {
        using var scope = _log.JoinOrBeginOperation(out _);

        var state = await _lockStore.ReadAsync(ct).ConfigureAwait(false);
        if (state?.Owner is null)
        {
            _log.Info(LogCategory.Lock, "no persisted lock state; nothing to recover");
        }
        else
        {
            // Who held it and when is the whole story behind a re-adopted lock: it tells the
            // user whether the work is theirs from before a reboot or a colleague's session.
            _log.Info(LogCategory.Lock,
                $"recovering {state.Bindings.Count} persisted lock(s) last held by " +
                $"{state.Owner.User}@{state.Owner.Machine} " +
                $"(pid {state.Owner.ProcessId}, renewed {state.Owner.RenewedUtc:u})");
        }

        var hashes = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var binding in _bindings.Values)
        {
            hashes[binding.Id] = (await ProjectHashAsync(binding, ct).ConfigureAwait(false),
                                  await ExportTreeHashAsync(binding, ct).ConfigureAwait(false));
        }

        var decisions = policy.Evaluate(state, hashes);

        foreach (var decision in decisions)
        {
            if (!_locks.ContainsKey(decision.BindingId))
            {
                _log.Warn(LogCategory.Lock,
                    $"{decision.BindingId}: a lock was recovered for a binding this " +
                    "repository no longer tracks; ignoring it");
                continue;
            }

            var persisted = state?.Bindings.GetValueOrDefault(decision.BindingId);

            _locks[decision.BindingId] = decision.Outcome switch
            {
                LockRecovery.ReadoptedAfterRestart when persisted is not null => persisted,

                // Releasing the DIRECTION is not the same as forgetting what was last
                // exported, and conflating them cost an export on every single launch.
                //
                // This branch used to hand back a blank BindingLock, which wiped LastSynced.
                // The commit gate compares the project's hash against LastSynced.ProjectHash,
                // so an empty one reads as "the project has changed since it was last
                // exported" — for a project nobody had touched. Every open therefore ran
                // CODESYS for 17–31 s to rebuild a mirror that was already correct, and left
                // the binding holding a fresh ProjectAuthoritative lock, so the project sat
                // in the panel reading "project ahead" until somebody pushed.
                //
                // Invisible while exporting was a button somebody pressed. It became a
                // twenty-second cost on every launch the moment the catch-up made it
                // automatic, which is how it was found.
                _ => new BindingLock
                {
                    Direction = SyncDirection.Clean,
                    Baseline = persisted?.Baseline ?? new SyncBaseline(),
                    LastSynced = persisted?.LastSynced ?? new SyncedState(),
                },
            };

            // Readoption is the consequential one: it means unsynced work survived a crash
            // or a reboot and the direction is still owned. Logged at Warning so it stands
            // out in a file the user is reading precisely because something went wrong.
            _log.Log(decision.Outcome == LockRecovery.ReadoptedAfterRestart
                    ? LogLevel.Warning
                    : LogLevel.Information,
                LogCategory.Lock,
                $"{decision.BindingId}: {decision.Outcome} — {decision.Explanation}; " +
                $"now {_locks[decision.BindingId].Direction}");
        }

        return decisions;
    }

    /// <summary>A change was detected. Decide what, if anything, to do about it.</summary>
    /// <remarks>
    /// The loop is the coalescer's contract, and it used to be missing.
    /// <see cref="JobCoalescer.OnJobCompleted"/> returns <c>true</c> to mean <i>a change
    /// arrived while that run was going and must be handled now</i>, and its result was
    /// discarded — so the follow-up never ran, and worse, the coalescer had already marked
    /// the slot as running again on the strength of the caller obeying it. Every later change
    /// on that binding then answered <c>Queue</c> and then <c>Coalesced</c> for the rest of
    /// the session. Nothing noticed because the commit gate is synchronous and skips the
    /// coalescer entirely, so exporting still worked at the one moment anybody checked.
    /// <para>
    /// The follow-up re-enters the whole pipeline rather than re-running the export directly:
    /// the hash gate, the occupancy probe and the direction lock all have to be asked again
    /// about a file that has changed since. Re-running the body would also export a scratch
    /// copy that the previous run has already deleted.
    /// </para>
    /// </remarks>
    public Task<SyncOutcome> HandleChangeAsync(SyncJob job, CancellationToken ct) =>
        RunLoopAsync(job, slotHeld: false, ct);

    /// <summary>
    /// Attempt the job, then keep going while the coalescer says a follow-up must start now.
    /// </summary>
    private async Task<SyncOutcome> RunLoopAsync(SyncJob job, bool slotHeld, CancellationToken ct)
    {
        while (true)
        {
            var (outcome, ranWithSlot) = await AttemptAsync(job, slotHeld, ct)
                .ConfigureAwait(false);

            if (!ranWithSlot || !_coalescers[job.BindingId].OnJobCompleted())
            {
                return outcome;
            }

            _log.Info(LogCategory.Sync,
                $"{job.BindingId}: handling the change that arrived while the last run was " +
                "in flight");

            // The slot is already ours: OnJobCompleted handed it straight back rather than
            // releasing it, which is what "must start now" means.
            slotHeld = true;
        }
    }

    /// <param name="slotHeld">
    /// True when the coalescer has already granted this job its slot — a queued follow-up, or
    /// a throttled job released by <see cref="RunDeferredAsync"/>. Such a job skips the
    /// coalescer question it has already been answered, and still completes the slot on the
    /// way out.
    /// </param>
    /// <returns>
    /// The outcome, and whether a toolchain run actually happened while holding the slot —
    /// which is what decides whether there is a slot to complete.
    /// </returns>
    private async Task<(SyncOutcome Outcome, bool RanWithSlot)> AttemptAsync(
        SyncJob job, bool slotHeld, CancellationToken ct)
    {
        // Joins the caller's scope when there is one — a button press already opened it —
        // and starts a fresh one when the trigger was a file watcher with nobody above it.
        using var scope = _log.JoinOrBeginOperation(out var operationId);

        _log.Debug(LogCategory.Sync,
            $"{job.BindingId}: {job.Source} change" +
            (string.IsNullOrEmpty(job.Reason) ? string.Empty : $" ({job.Reason})") +
            (job.Synchronous ? " [synchronous]" : string.Empty) +
            (job.Confirmed ? " [confirmed]" : string.Empty));

        if (!_bindings.TryGetValue(job.BindingId, out var binding) || !binding.Enabled)
        {
            return (Refuse(LogLevel.Warning, SyncVerdict.UnknownBinding, job.BindingId,
                "no enabled binding with that id"), slotHeld);
        }

        var state = StateOf(job.BindingId);

        // 1. Circuit breaker. First, because a tripped breaker means our own reasoning is
        //    no longer trusted and nothing below it should run.
        if (_breaker.IsTripped(job.BindingId))
        {
            return (Refuse(LogLevel.Warning, SyncVerdict.BlockedByCircuitBreaker, job.BindingId,
                "sync halted for this project after repeated cycles; acknowledge to resume",
                state), slotHeld);
        }

        // 2. Hash gate. Before the lock on purpose: an event carrying no actual change is
        //    noise, and reporting it as a lock violation would show the user a scary
        //    refusal for a no-op save.
        var (changed, currentHash) = await HasRealChangeAsync(binding, job.Source, ct)
            .ConfigureAwait(false);
        if (!changed)
        {
            // Debug, not Warning: this is the single most common outcome — every save that
            // did not actually alter the file lands here — and at Info it would drown the
            // log. The hash is included because "the gate says nothing changed but I know
            // I changed something" is otherwise unanswerable.
            return (Refuse(LogLevel.Debug, SyncVerdict.NoChange, job.BindingId,
                $"content is identical to the last synced state (hash {Short(currentHash)})",
                state), slotHeld);
        }

        // 3. Self-write suppression, for text only: an export writes into the tree the text
        //    watcher is watching. The project file is never written by us.
        if (job.Source == ChangeSource.ExportedText &&
            _suppressor.ShouldSuppress(binding.ExportPath, currentHash))
        {
            return (Refuse(LogLevel.Debug, SyncVerdict.SelfWriteSuppressed, job.BindingId,
                "this was the echo of our own export", state), slotHeld);
        }

        // 3b. UNMERGED PATHS.
        //
        //     After the hash gate and the suppressor, so a save that changed nothing and an
        //     echo of our own export are still dropped as what they are rather than reported
        //     as a conflict — and so the cost, one `git status`, is paid only for changes that
        //     are actually going somewhere.
        //
        //     Before the direction lock, deliberately. A conflicted tree is the more
        //     fundamental problem and has the clearer remedy; reporting it as a lock violation
        //     would send the user to push or discard when what they have to do is finish the
        //     merge. The guard above the UI orders these two the same way, for the same reason.
        if (await ConflictedPathsFor(binding, ct).ConfigureAwait(false) is { Count: > 0 } clashes)
        {
            return (Refuse(LogLevel.Information, SyncVerdict.BlockedByConflict, job.BindingId,
                $"{ProjectName(binding)} has {Files(clashes.Count)} with unresolved merge " +
                $"conflicts ({string.Join(", ", clashes.Take(3))}" +
                (clashes.Count > 3 ? ", …" : string.Empty) +
                "). Finish the merge and this will run on the next change.",
                state), slotHeld);
        }

        // 4. Direction lock.
        var trigger = job.Source == ChangeSource.Project
            ? SyncTrigger.ProjectChanged
            : SyncTrigger.TextChanged;
        var transition = SyncStateMachine.Next(state, trigger);
        if (!transition.Allowed)
        {
            // The one refusal the user is most likely to be confused by, so it carries the
            // state that did the refusing as well as the sentence explaining it.
            return (Refuse(LogLevel.Warning, SyncVerdict.BlockedByLock, job.BindingId,
                $"{transition.Reason} (lock is {state})", transition.State), slotHeld);
        }

        // 5. Import is detected automatically but applied only on confirmation.
        if (job.Source == ChangeSource.ExportedText &&
            _options.RequireImportConfirmation && !job.Confirmed)
        {
            return (Refuse(LogLevel.Information, SyncVerdict.AwaitingConfirmation, job.BindingId,
                "exported text changed; review the diff and confirm before importing", state), slotHeld);
        }

        // 5b. IS THE PROJECT OPEN IN ITS IDE?
        //
        // Before the coalescer and the breaker, deliberately. A wait must consume neither:
        // a breaker fed by these would trip after a few saves and halt the project for the
        // rest of the session, turning the wait into exactly the sticky failure this exists to
        // avoid. It is also before the lock, because refusing after taking it would leave the
        // direction claimed for work that never ran.
        //
        // After the hash gate and the direction lock, though: "nothing changed" and "the lock
        // forbids this direction" are more specific answers, and reporting the IDE instead
        // would be noise on every settle while somebody has CODESYS open.
        //
        // It costs one file read, against finding out 17-31 s later from the toolchain itself
        // — and finding out then as a failure, which marks the binding failed and stops
        // everything until somebody clears it by hand.
        //
        // Somebody saving and leaving CODESYS open is the most common working state there is.
        // It is a wait, not a fault.
        var occupancy = _occupancy is null
            ? ProjectOccupancy.Free
            : await _occupancy.CheckAsync(binding, Absolute(binding.ProjectPath), ct)
                .ConfigureAwait(false);

        string? scratchProject = null;

        if (occupancy.IsHeld)
        {
            // Import writes into the .project itself. Even if the toolchain allowed it, the
            // IDE holds its own copy in memory and the next save would silently revert what
            // was imported - so this direction genuinely has to wait.
            if (job.Source == ChangeSource.ExportedText)
            {
                return (Refuse(LogLevel.Information, SyncVerdict.BlockedByOpenProject,
                    job.BindingId,
                    $"{ProjectName(binding)} is open in the IDE ({occupancy.Owner}), so the " +
                    "edited text cannot be imported yet. Close it and the import will run.",
                    state), slotHeld);
            }

            // Export only reads, and the lock is a sidecar on the path - so a copy at another
            // path is unencumbered and exports byte-identically. Measured; see STATUS.
            scratchProject = await CopyOpenProjectAsync(binding, ct).ConfigureAwait(false);

            if (scratchProject is null)
            {
                return (Refuse(LogLevel.Warning, SyncVerdict.BlockedByOpenProject, job.BindingId,
                    $"{ProjectName(binding)} is open in the IDE ({occupancy.Owner}) and could " +
                    "not be copied aside, so the export was not attempted.",
                    state), slotHeld);
            }
        }

        // 6. Coalescing. Skipped for a synchronous job, because the commit gate must run now
        //    and block rather than queue behind a background export — and skipped for a job
        //    that already holds its slot, which has been through this question once.
        var granted = slotHeld;

        if (!job.Synchronous && !slotHeld)
        {
            var action = _coalescers[job.BindingId].OnChangeSettled();
            switch (action)
            {
                case CoalesceAction.Coalesced:
                    return (Refuse(LogLevel.Debug, SyncVerdict.Coalesced, job.BindingId,
                        "a sync is already running and a follow-up is queued", state), false);

                case CoalesceAction.Throttled:
                    // Remembered, not just refused. The pending flag inside the coalescer
                    // guarantees a final export "once things go quiet", and nothing was
                    // asking it — see RunDeferredAsync.
                    _deferred[job.BindingId] = job;
                    return (Refuse(LogLevel.Debug, SyncVerdict.Throttled, job.BindingId,
                        "throttled; a final sync will run once changes settle", state), false);

                case CoalesceAction.Queue:
                    return (Refuse(LogLevel.Debug, SyncVerdict.Coalesced, job.BindingId,
                        "queued behind the running sync", state), false);
            }

            granted = true;
        }

        if (!_breaker.TryRecord(job.BindingId))
        {
            // This is the breaker tripping, not a breaker that was already tripped: the
            // threshold was just crossed. Worth an explicit line — it is the loop defence
            // of last resort, and its firing means the other two failed.
            _log.Warn(LogCategory.Sync,
                $"{job.BindingId}: circuit breaker tripped after " +
                $"{_breaker.CountInWindow(job.BindingId)} syncs within " +
                $"{_options.CircuitBreakerWindow.TotalSeconds:F0}s — halting sync for this project");

            return (Refuse(LogLevel.Warning, SyncVerdict.BlockedByCircuitBreaker, job.BindingId,
                "sync halted for this project after repeated cycles; acknowledge to resume",
                state), granted);
        }

        // 7. TAKE THE LOCK BEFORE DOING THE WORK, and persist it.
        //
        // Not an ordering preference - a correctness requirement. If the lock were only
        // recorded on success, a crash or a kill during a 30 s export would leave unsynced
        // work with nothing guarding it: the next session would find a clean state, and the
        // opposite direction would be free to overwrite it.
        //
        // It also makes the state machine coherent. ExportSucceeded is only defined as a
        // transition FROM ProjectAuthoritative, so the direction has to be claimed first.
        await ApplyAsync(job.BindingId, trigger,
            _locks[job.BindingId].LastSynced.ProjectHash, ct).ConfigureAwait(false);

        var operation = job.Source == ChangeSource.Project
            ? PlcOperation.Export
            : PlcOperation.Import;

        // Said out loud the moment the work actually begins, and not one gate earlier.
        //
        // Every refusal above returns before this point, which is the whole reason it is a
        // notification rather than something the caller wraps around the call: a save that
        // changed nothing, was coalesced, was throttled or was refused by the lock is the
        // common case, and lighting a progress bar for the 200 ms it takes to decide that
        // would flicker on every keystroke-triggered settle.
        var notice = new SyncRunNotice(job.BindingId, operation, operationId);
        RunStarted?.Invoke(notice);

        try
        {
            var outcome = operation == PlcOperation.Export
                ? await ExportAsync(binding, transition, scratchProject, operationId, ct)
                    .ConfigureAwait(false)
                : await ImportAsync(binding, transition, operationId, ct).ConfigureAwait(false);

            // The slot is completed by the caller, which is the only place that can act on
            // "a follow-up must start now" — it has to await one.
            return (outcome, !job.Synchronous);
        }
        finally
        {
            RunFinished?.Invoke(notice);
        }
    }

    /// <summary>
    /// Run whatever the throttle deferred, if its interval has passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The missing driver for <see cref="JobCoalescer.TryStartThrottled"/>, which was written
    /// for "the idle tick" and had <b>no caller at all</b> — so a change arriving within the
    /// 30-second minimum interval was recorded as pending and then waited for the next save
    /// or the commit gate to notice it. A save-happy engineer who then stopped saving got no
    /// export until they committed.
    /// </para>
    /// <para>
    /// Only the throttled case needs this. A change that arrived <i>during</i> a run is
    /// handled by the loop in <see cref="HandleChangeAsync"/> the moment that run ends, which
    /// is sooner than any tick.
    /// </para>
    /// <para>
    /// The deferred job re-enters the full pipeline, so the file is re-hashed and the IDE
    /// re-probed: by the time the interval expires, the reason to run may have gone away.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<SyncOutcome>> RunDeferredAsync(CancellationToken ct)
    {
        var outcomes = new List<SyncOutcome>();

        foreach (var (bindingId, job) in _deferred.ToList())
        {
            if (!_coalescers[bindingId].TryStartThrottled())
            {
                continue;
            }

            _deferred.Remove(bindingId);
            _log.Info(LogCategory.Sync,
                $"{bindingId}: the throttle has expired; running the change it deferred");

            // Enters holding the slot TryStartThrottled just granted.
            outcomes.Add(await RunLoopAsync(job, slotHeld: true, ct).ConfigureAwait(false));
        }

        return outcomes;
    }


    /// <param name="scratchProject">
    /// A copy to export instead of the project itself, when the IDE has the original open.
    /// Deleted when the run finishes, however it finishes.
    /// </param>
    private async Task<SyncOutcome> ExportAsync(
        ProjectBinding binding, TransitionResult transition, string? scratchProject,
        string operationId, CancellationToken ct)
    {
        var started = _clock.UtcNow;
        var source = scratchProject ?? Absolute(binding.ProjectPath);

        var job = new PlcJob
        {
            Operation = PlcOperation.Export,

            // The SAME id the log scope is using, rather than the record's random default.
            //
            // The runner tags every progress report with this, and the heartbeat is keyed by
            // it. Left to default they were two unrelated random strings, so
            // HeartbeatService.Report found no tracker and dropped every phase and count the
            // toolchain produced — on every path, including the commit gate, where the
            // heartbeat did exist and could only ever show elapsed seconds.
            OperationId = operationId,
            Profile = binding.Profile ?? string.Empty,
            Projects =
            [
                new PlcProjectJob
                {
                    BindingId = binding.Id,
                    ProjectPath = source,

                    // Unchanged. The copy is only ever the thing being read; the mirror it
                    // produces belongs where it always did.
                    Path = Absolute(binding.ExportPath),
                },
            ],
        };

        // Said out loud when it is a copy. Otherwise the log describes an export of a file at
        // a path the user cannot find, and nothing explains why.
        _log.Info(LogCategory.Sync,
            $"{binding.Id}: exporting " +
            (scratchProject is null
                ? binding.ProjectPath
                : $"a copy of {binding.ProjectPath} (the IDE has the original open)") +
            $" → {binding.ExportPath}" +
            (string.IsNullOrEmpty(binding.Profile) ? string.Empty : $" [{binding.Profile}]"));

        try
        {

        var result = await _toolchain.RunAsync(job, _progress, ct).ConfigureAwait(false);
        var elapsed = _clock.UtcNow - started;

        if (!result.Succeeded)
        {
            var reason = result.FailureReason ?? "the export did not complete";
            _log.Error(LogCategory.Sync,
                $"{binding.Id}: export failed after {elapsed.TotalSeconds:F1}s — {reason}");

            await ApplyAsync(binding.Id, SyncTrigger.ExportFailed, string.Empty, ct)
                .ConfigureAwait(false);
            return new SyncOutcome
            {
                Verdict = SyncVerdict.Failed,
                BindingId = binding.Id,
                Explanation = reason,
                State = SyncDirection.Failed,
                Duration = elapsed,
                Warnings = LogWarnings(binding.Id, result),
            };
        }

        // Register what we wrote so the text watcher's echo is swallowed rather than being
        // mistaken for a user edit.
        var mirrorHash = await ExportTreeHashAsync(binding, ct).ConfigureAwait(false);
        _suppressor.Register(binding.ExportPath, mirrorHash);

        var projectHash = await ProjectHashAsync(binding, ct).ConfigureAwait(false);
        await ApplyAsync(binding.Id, SyncTrigger.ExportSucceeded, projectHash, ct)
            .ConfigureAwait(false);

        var changed = result.ChangedPaths;
        _log.Info(LogCategory.Sync,
            $"{binding.Id}: exported {changed.Count} object(s) in {elapsed.TotalSeconds:F1}s");

        // Per-file at Debug. On a large project this is hundreds of lines, which is why it
        // is not Info — but it is the only record of what an export actually touched.
        foreach (var file in changed)
        {
            _log.Debug(LogCategory.Sync, $"{binding.Id}: wrote {file}");
        }

        return new SyncOutcome
        {
            Verdict = SyncVerdict.Completed,
            BindingId = binding.Id,
            Explanation = transition.Reason,
            State = _locks[binding.Id].Direction,
            ChangedFiles = changed,
            Warnings = LogWarnings(binding.Id, result),
            Duration = elapsed,
        };
        }
        finally
        {
            DiscardScratchProject(scratchProject);
        }
    }

    /// <summary>
    /// Copy a project the IDE has open, so the export has something it is allowed to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The filename is preserved.</b> The manifest records the project's basename, so a copy
    /// called anything else would rewrite that field and churn the mirror on every export while
    /// the IDE happened to be open.
    /// </para>
    /// <para>
    /// Under <c>.versio/local/</c>, which is gitignored, so the copy never reaches git and
    /// never appears in the panel. Deleted as soon as the export finishes: it is scaffolding,
    /// not a backup, and the backups live next door under their own name.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Which of the tree's unmerged paths belong to this binding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sides count. A conflict in the mirror is the obvious one — importing text holding
    /// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> markers writes them into a structured project file
    /// that will not compile afterwards. A conflict on the <c>.project</c> itself matters just
    /// as much: exporting from a binary git could not merge would produce a mirror of whichever
    /// half won, and record it as the truth.
    /// </para>
    /// <para>
    /// A conflict elsewhere in the repository is somebody else's business. A README both sides
    /// edited has nothing to do with this project, and halting its sync over one would be the
    /// same over-reach as releasing every direction lock because one unrelated file was
    /// discarded.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ConflictedPathsFor(
        ProjectBinding binding, CancellationToken ct)
    {
        var unmerged = await _conflicts.UnmergedPathsAsync(ct).ConfigureAwait(false);

        if (unmerged.Count == 0)
        {
            return [];
        }

        return [.. unmerged.Where(path => Owns(binding, path))];
    }

    /// <summary>Does this binding's project or mirror contain the given repo-relative path?</summary>
    /// <remarks>
    /// The project is matched exactly and the mirror by prefix, in that order, and the prefix
    /// carries its separator: <c>plc/A.project</c> sits beside <c>plc/A</c>, and a careless
    /// prefix test would have the mirror claim the project — the same trap
    /// <c>SyncViewModel.Locate</c> documents on the other side of the seam.
    /// </remarks>
    private static bool Owns(ProjectBinding binding, string repoRelativePath)
    {
        var path = repoRelativePath.Replace('\\', '/');

        return string.Equals(path, binding.ProjectPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(binding.ExportPath.TrimEnd('/') + '/', StringComparison.OrdinalIgnoreCase);
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    private async Task<string?> CopyOpenProjectAsync(ProjectBinding binding, CancellationToken ct)
    {
        var source = Absolute(binding.ProjectPath);
        var folder = Combine(".versio", "local", "open", binding.Id);
        var name = Path.GetFileName(binding.ProjectPath.Replace('/', Path.DirectorySeparatorChar));
        var destination = Path.Combine(folder, name);

        try
        {
            _fs.CreateDirectory(folder);
            await _fs.CopyAsync(source, destination, ct).ConfigureAwait(false);

            _log.Info(LogCategory.Sync,
                $"{binding.Id}: copied {binding.ProjectPath} to {destination} because the IDE " +
                "has it open; the export reads the copy");

            return destination;
        }
        catch (IOException ex)
        {
            // Reported, not thrown. A project that cannot be copied is a project that has to
            // wait, and a wait is not a failure the user has to acknowledge.
            _log.Warn(LogCategory.Sync,
                $"{binding.Id}: could not copy {binding.ProjectPath} aside: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Clear the scratch folder. Never fails the export it belongs to.
    /// </summary>
    /// <remarks>
    /// <b>Everything in the folder, not just the copy we made.</b> Opening a project makes the
    /// toolchain write its own files beside it, and they outlive the run: measured on CODESYS
    /// 3.5.22.20, an export leaves <c>&lt;name&gt;-AllUsers.opt</c> and
    /// <c>&lt;name&gt;-&lt;user&gt;-&lt;machine&gt;.opt</c>. Deleting only the file we created
    /// leaves those behind for good. The folder is ours and holds nothing else, so emptying it
    /// is both safe and the only way to be sure — including of whatever a later version adds.
    /// </remarks>
    private void DiscardScratchProject(string? path)
    {
        if (path is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (folder is null)
        {
            return;
        }

        foreach (var file in SafeEnumerate(folder))
        {
            try
            {
                _fs.DeleteFile(file);
                _log.Debug(LogCategory.Sync, $"removed the scratch file {file}");
            }
            catch (IOException ex)
            {
                // Under .versio/local/, so a leak costs disk and nothing else. The next export
                // of this binding overwrites the copy in place regardless.
                _log.Debug(LogCategory.Sync, $"could not remove {file}: {ex.Message}");
            }
        }
    }

    private IEnumerable<string> SafeEnumerate(string folder)
    {
        try
        {
            return _fs.DirectoryExists(folder) ? _fs.EnumerateFiles(folder) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task<SyncOutcome> ImportAsync(
        ProjectBinding binding, TransitionResult transition, string operationId,
        CancellationToken ct)
    {
        var started = _clock.UtcNow;

        try
        {
            await BackUpProjectAsync(binding, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Refuse rather than proceed. Running the one operation that can destroy the
            // project having failed to make it recoverable is the worst of both.
            await ApplyAsync(binding.Id, SyncTrigger.ImportFailed, string.Empty, ct)
                .ConfigureAwait(false);

            var why = $"could not back up {binding.ProjectPath} before importing: {ex.Message}";
            _log.Error(LogCategory.Sync, why, ex);

            return new SyncOutcome
            {
                Verdict = SyncVerdict.Failed,
                BindingId = binding.Id,
                Explanation = why,
                State = SyncDirection.Failed,
                Duration = _clock.UtcNow - started,
            };
        }

        var job = new PlcJob
        {
            Operation = PlcOperation.Import,
            OperationId = operationId,
            Profile = binding.Profile ?? string.Empty,
            Projects =
            [
                new PlcProjectJob
                {
                    BindingId = binding.Id,
                    ProjectPath = Absolute(binding.ProjectPath),
                    Path = Absolute(binding.ExportPath),
                },
            ],
        };

        // Warning, not Information. This is the direction that writes into a binary project
        // file the user cannot diff afterwards, and it is the line they will look for when
        // asking what touched their project.
        _log.Warn(LogCategory.Sync,
            $"{binding.Id}: importing {binding.ExportPath} → {binding.ProjectPath}" +
            (string.IsNullOrEmpty(binding.Profile) ? string.Empty : $" [{binding.Profile}]"));

        var result = await _toolchain.RunAsync(job, _progress, ct).ConfigureAwait(false);
        var elapsed = _clock.UtcNow - started;

        if (!result.Succeeded)
        {
            var reason = result.FailureReason ?? "the import did not complete";
            _log.Error(LogCategory.Sync,
                $"{binding.Id}: import failed after {elapsed.TotalSeconds:F1}s — {reason}");

            await ApplyAsync(binding.Id, SyncTrigger.ImportFailed, string.Empty, ct)
                .ConfigureAwait(false);
            return new SyncOutcome
            {
                Verdict = SyncVerdict.Failed,
                BindingId = binding.Id,
                Explanation = reason,
                State = SyncDirection.Failed,
                Duration = elapsed,
                Warnings = LogWarnings(binding.Id, result),
            };
        }

        var projectHash = await ProjectHashAsync(binding, ct).ConfigureAwait(false);
        await ApplyAsync(binding.Id, SyncTrigger.ImportSucceeded, projectHash, ct)
            .ConfigureAwait(false);

        _log.Info(LogCategory.Sync,
            $"{binding.Id}: imported in {elapsed.TotalSeconds:F1}s; " +
            $"project is now {Short(projectHash)}");

        return new SyncOutcome
        {
            Verdict = SyncVerdict.Completed,
            BindingId = binding.Id,
            Explanation = transition.Reason,
            State = _locks[binding.Id].Direction,
            Warnings = LogWarnings(binding.Id, result),
            Duration = elapsed,
        };
    }

    /// <summary>
    /// The commit gate: refuse to commit while any binding's export is stale.
    /// </summary>
    /// <remarks>
    /// This is what converts a dropped watcher event from a correctness problem into a UX
    /// one — and since <c>On git action</c> became the default sync mode, it is the primary
    /// path by which the export tree is written at all.
    /// </remarks>
    public async Task<CommitGateResult> EvaluateCommitGateAsync(CancellationToken ct)
    {
        var freshness = new List<BindingFreshness>();
        foreach (var binding in _bindings.Values)
        {
            freshness.Add(new BindingFreshness
            {
                BindingId = binding.Id,
                CurrentProjectHash = await ProjectHashAsync(binding, ct).ConfigureAwait(false),
                ExportedProjectHash = _locks[binding.Id].LastSynced.ProjectHash,
                Enabled = binding.Enabled,
                InFailedState = _locks[binding.Id].Direction == SyncDirection.Failed,
            });
        }

        var result = CommitGate.Evaluate(freshness);

        // The gate is the primary path by which the export tree is written, so its verdict
        // is part of the narrative of every commit, not a diagnostic.
        _log.Log(result.Decision == GateDecision.Proceed ? LogLevel.Debug : LogLevel.Information,
            LogCategory.Sync,
            $"commit gate: {result.Decision} — {result.Explanation}" +
            (result.StaleBindings.Count == 0
                ? string.Empty
                : $" [{string.Join(", ", result.StaleBindings)}]"));

        return result;
    }

    /// <summary>Run a blocking export for every stale binding, then re-check the gate.</summary>
    public async Task<IReadOnlyList<SyncOutcome>> SatisfyCommitGateAsync(CancellationToken ct)
    {
        using var scope = _log.JoinOrBeginOperation(out _);

        var gate = await EvaluateCommitGateAsync(ct).ConfigureAwait(false);
        var outcomes = new List<SyncOutcome>();

        if (gate.Decision != GateDecision.ExportRequired)
        {
            return outcomes;
        }

        _log.Info(LogCategory.Sync,
            $"commit gate requires an export of {gate.StaleBindings.Count} project(s); " +
            "running them synchronously before the commit");

        foreach (var bindingId in gate.StaleBindings)
        {
            outcomes.Add(await HandleChangeAsync(new SyncJob
            {
                BindingId = bindingId,
                Source = ChangeSource.Project,
                Synchronous = true,
                Reason = "commit gate",
            }, ct).ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>A push succeeded: release every binding it advanced.</summary>
    /// <remarks>
    /// Push, not commit. Until work is pushed nobody else can see it, so the direction is
    /// still meaningfully owned. And push is repo-wide, so it releases <b>every</b> binding,
    /// not just the one last looked at.
    /// </remarks>
    public async Task ReleaseAfterPushAsync(string headCommit, CancellationToken ct)
    {
        using var scope = _log.JoinOrBeginOperation(out _);

        var held = _locks.Where(kv => SyncStateMachine.HoldsLock(kv.Value.Direction))
                         .Select(kv => kv.Key)
                         .ToList();

        _log.Info(LogCategory.Lock, held.Count == 0
            ? $"push succeeded at {Short(headCommit)}; no locks were held"
            : $"push succeeded at {Short(headCommit)}; releasing {held.Count} lock(s): " +
              string.Join(", ", held));

        foreach (var binding in _bindings.Values)
        {
            await ApplyAsync(binding.Id, SyncTrigger.PushSucceeded,
                await ProjectHashAsync(binding, ct).ConfigureAwait(false), ct,
                headCommit).ConfigureAwait(false);
        }
    }

    public Task ReleaseAfterDiscardAsync(string bindingId, CancellationToken ct)
    {
        _log.Warn(LogCategory.Lock,
            $"{bindingId}: changes discarded; releasing the direction lock");
        return ApplyAsync(bindingId, SyncTrigger.ChangesDiscarded, string.Empty, ct);
    }

    public Task AcknowledgeFailureAsync(string bindingId, CancellationToken ct)
    {
        // Worth recording that a human intervened: everything after this point happens
        // because someone chose to clear a failure, not because the app decided to retry.
        _log.Info(LogCategory.Sync,
            $"{bindingId}: failure acknowledged; resetting the circuit breaker and coalescer");

        _breaker.Reset(bindingId);
        _coalescers[bindingId].Reset();
        return ApplyAsync(bindingId, SyncTrigger.FailureAcknowledged, string.Empty, ct);
    }

    private async Task ApplyAsync(string bindingId, SyncTrigger trigger, string projectHash,
                                  CancellationToken ct, string? headCommit = null)
    {
        if (!_locks.TryGetValue(bindingId, out var current))
        {
            _log.Warn(LogCategory.Lock,
                $"{bindingId}: {trigger} arrived for a binding that is not tracked; ignored");
            return;
        }

        var transition = SyncStateMachine.Next(current.Direction, trigger);
        if (!transition.Allowed)
        {
            // Not an error — the state machine refusing a trigger is how the lock does its
            // job — but it means a requested change did not happen, so it is never silent.
            _log.Debug(LogCategory.Lock,
                $"{bindingId}: {trigger} refused from {current.Direction} — {transition.Reason}");
            return;
        }

        var mirrorHash = _bindings.TryGetValue(bindingId, out var binding)
            ? await ExportTreeHashAsync(binding, ct).ConfigureAwait(false)
            : current.LastSynced.MirrorHash;

        // A successful sync moves LastSynced only. The mirror now reflects the project, but
        // nothing has been published, so the lock still guards real work.
        var synced = trigger is SyncTrigger.ExportSucceeded or SyncTrigger.ImportSucceeded
            ? new SyncedState { ProjectHash = projectHash, MirrorHash = mirrorHash }
            : current.LastSynced;

        // A release moves the baseline: this is now the state the world has seen.
        var baseline = transition.State == SyncDirection.Clean
            ? new SyncBaseline
            {
                HeadCommit = headCommit ?? current.Baseline.HeadCommit,
                ProjectHash = projectHash,
                MirrorHash = mirrorHash,
            }
            : current.Baseline;

        _locks[bindingId] = current with
        {
            Direction = transition.State,
            AcquiredUtc = transition.State == SyncDirection.Clean ? null : _clock.UtcNow,
            Reason = transition.Reason,
            Baseline = baseline,
            LastSynced = transition.State == SyncDirection.Clean
                ? new SyncedState { ProjectHash = projectHash, MirrorHash = mirrorHash }
                : synced,
        };

        // Only when it actually moved. Re-stating "still ProjectAuthoritative" on every
        // further edit is the kind of line that makes a log unreadable.
        if (current.Direction != transition.State)
        {
            _log.Info(LogCategory.Lock,
                $"{bindingId}: {current.Direction} → {transition.State} on {trigger} " +
                $"({transition.Reason})");
        }
        else
        {
            _log.Debug(LogCategory.Lock,
                $"{bindingId}: {trigger} accepted, still {transition.State} ({transition.Reason})");
        }

        await PersistAsync(ct).ConfigureAwait(false);
    }

    private Task PersistAsync(CancellationToken ct) =>
        _lockStore.WriteAsync(new LockState
        {
            RepoPath = _repoRoot,
            Owner = _owner with { RenewedUtc = _clock.UtcNow },
            Bindings = new Dictionary<string, BindingLock>(_locks, StringComparer.Ordinal),
        }, ct);

    private async Task<(bool Changed, string Hash)> HasRealChangeAsync(
        ProjectBinding binding, ChangeSource source, CancellationToken ct)
    {
        // LastSynced, not Baseline: the question here is "has anything changed since the
        // mirror was last written", which an export answers and a push does not.
        var synced = _locks[binding.Id].LastSynced;
        if (source == ChangeSource.Project)
        {
            var hash = await ProjectHashAsync(binding, ct).ConfigureAwait(false);
            return (hash != synced.ProjectHash, hash);
        }

        var mirror = await ExportTreeHashAsync(binding, ct).ConfigureAwait(false);
        return (mirror != synced.MirrorHash, mirror);
    }

    /// <summary>
    /// Copy the project file aside before an import writes into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Import is the one operation that writes the file nobody can rebuild by hand.</b> An
    /// export can always be regenerated from the project; a project damaged by an import is
    /// gone unless git happens to have it, and it will not if the damage lands on top of
    /// changes that were never committed. Now that import runs unattended there is nobody
    /// watching it happen, so the copy is not optional.
    /// </para>
    /// <para>
    /// Under <c>.versio/local/</c>, which is gitignored: these are a local undo, not history,
    /// and committing megabytes of binary project snapshots would be its own problem. The last
    /// <see cref="BackupsKept"/> survive - enough to walk back past a bad import, bounded so a
    /// busy afternoon does not fill a disk.
    /// </para>
    /// <para>
    /// A failure here <b>stops the import</b>. Proceeding without the copy would be doing the
    /// dangerous half of the operation having skipped the part that makes it recoverable.
    /// </para>
    /// </remarks>
    private async Task<string?> BackUpProjectAsync(ProjectBinding binding, CancellationToken ct)
    {
        var source = Absolute(binding.ProjectPath);
        if (!_fs.FileExists(source))
        {
            // Nothing to lose yet. An import that creates the project is unusual but not
            // wrong, and refusing it over a missing backup would be.
            return null;
        }

        var folder = Combine(".versio", "local", "backups", binding.Id);
        var stamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = Path.GetFileName(binding.ProjectPath.Replace('/', Path.DirectorySeparatorChar));
        var destination = Path.Combine(folder, $"{stamp}-{name}");

        _fs.CreateDirectory(folder);
        await _fs.CopyAsync(source, destination, ct).ConfigureAwait(false);

        PruneBackups(folder);

        _log.Info(LogCategory.Sync, $"{binding.Id}: project backed up to {destination}");
        return destination;
    }

    /// <summary>Keep the most recent backups and drop the rest.</summary>
    /// <remarks>
    /// Each deletion is logged by name. These are somebody's undo history, and a backup that
    /// vanished without a line saying so is indistinguishable from one that was never made -
    /// which is the question being asked at precisely the moment they go looking.
    /// </remarks>
    private void PruneBackups(string folder)
    {
        try
        {
            var existing = _fs.EnumerateFiles(folder)
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(BackupsKept)
                .ToList();

            foreach (var stale in existing)
            {
                _fs.DeleteFile(stale);
                _log.Info(LogCategory.Sync, $"pruned old project backup {stale}");
            }
        }
        catch (IOException ex)
        {
            // Pruning is housekeeping. Failing it must never fail the import that just
            // succeeded in making a backup.
            _log.Debug(LogCategory.Sync, $"could not prune old backups: {ex.Message}");
        }
    }

    private async Task<string> ProjectHashAsync(ProjectBinding binding, CancellationToken ct)
    {
        var path = Absolute(binding.ProjectPath);
        if (!_fs.FileExists(path))
        {
            return string.Empty;
        }

        var bytes = await _fs.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return ContentHash.Of(bytes);
    }

    /// <summary>
    /// Hash of the export tree, over the files it actually contains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This used to hash <c>_manifest.json</c> alone</b>, on the reasoning that the
    /// manifest carries a canonical hash per object, so hashing it was equivalent to hashing
    /// the tree at the cost of one small read rather than walking megabytes of visualisation
    /// XML on every watcher event.
    /// </para>
    /// <para>
    /// The reasoning was wrong in the direction that mattered. <b>The manifest is written by
    /// an export and by nothing else.</b> An engineer editing <c>Simulation.st</c> — the
    /// entire purpose of the mirror — left it byte-identical, so the hash gate reported
    /// <see cref="SyncVerdict.NoChange"/> and the import was refused before the direction
    /// lock was ever consulted. The scenario suite did not catch it because it simulated a
    /// text edit by rewriting the manifest, which is the one edit no human ever makes.
    /// </para>
    /// <para>
    /// The cost concern was real but much smaller than assumed: this is a SHA-256 over a few
    /// megabytes, tens of milliseconds, and the watcher coalesces bursts into one call. Being
    /// slightly slower to answer correctly beats being fast and wrong.
    /// </para>
    /// </remarks>
    private async Task<string> ExportTreeHashAsync(ProjectBinding binding, CancellationToken ct)
    {
        var root = Combine(binding.ExportPath);
        if (!_fs.DirectoryExists(root))
        {
            // Empty, not a hash of nothing: "never exported" and "exported to an empty tree"
            // are different states and the baseline has to be able to tell them apart.
            return string.Empty;
        }

        var files = new List<(string Path, string Hash)>();
        var bytesRead = 0L;

        foreach (var file in _fs.EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await _fs.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            bytesRead += bytes.Length;
            files.Add((Path.GetRelativePath(root, file), ContentHash.Of(bytes)));
        }

        var hash = files.Count == 0 ? string.Empty : ContentHash.OfTree(files);

        // Debug, not Info: this runs on every change event and would drown the log at a
        // higher level. It is here because "the gate says nothing changed, but I edited a
        // file" is otherwise unanswerable — the file count says whether the tree was even
        // seen, and the hash says whether it moved.
        _log.Debug(LogCategory.Sync,
            $"{binding.Id}: hashed {files.Count} file(s), {bytesRead:N0} bytes under " +
            $"{binding.ExportPath} -> {Short(hash)}");

        return hash;
    }

    /// <summary>
    /// Build a refusal, logging it on the way out.
    /// </summary>
    /// <remarks>
    /// Every refusal in <see cref="HandleChangeAsync"/> goes through here rather than
    /// calling <see cref="SyncOutcome.Refused"/> directly, so that a branch added later
    /// cannot be a silent one. A refusal produces no visible effect by definition — the user
    /// saved and nothing happened — which makes it the outcome most in need of a log line
    /// and the easiest one to forget to write.
    /// </remarks>
    private SyncOutcome Refuse(LogLevel level, SyncVerdict verdict, string bindingId,
                               string why, SyncDirection state = SyncDirection.Clean)
    {
        _log.Log(level, LogCategory.Sync, $"{bindingId}: {verdict} — {why}");
        return SyncOutcome.Refused(verdict, bindingId, why, state);
    }

    /// <summary>
    /// Log the run's warnings, and hand them back for the caller to show.
    /// </summary>
    /// <remarks>
    /// A warning means an object was skipped or degraded — the run "succeeded" but is not
    /// complete. The UI shows these, but the UI is transient and the log is what survives to
    /// the bug report. The adapter has already turned the vendor's own format into sentences;
    /// nothing here knows what a CODESYS warning code looks like.
    /// </remarks>
    private IReadOnlyList<string> LogWarnings(string bindingId, PlcResult result)
    {
        foreach (var warning in result.Warnings)
        {
            _log.Warn(LogCategory.Plc, $"{bindingId}: {warning}");
        }

        return result.Warnings;
    }

    /// <summary>First 12 characters of a hash or commit id — enough to compare two by eye,
    /// short enough not to dominate the line.</summary>
    private static string Short(string hash) =>
        string.IsNullOrEmpty(hash) ? "(none)"
        : hash.Length <= 12 ? hash
        : hash[..12];

    /// <summary>The project's filename, which is what somebody calls it.</summary>
    private static string ProjectName(ProjectBinding binding) =>
        Path.GetFileName(binding.ProjectPath.Replace('/', Path.DirectorySeparatorChar));

    private string Absolute(string relative) =>
        Path.IsPathRooted(relative) ? relative : Combine(relative);

    private string Combine(params string[] parts) =>
        Path.Combine([_repoRoot, .. parts.Select(p => p.Replace('/', Path.DirectorySeparatorChar))]);
}
