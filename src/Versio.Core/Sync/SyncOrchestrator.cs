using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Plc.Codesys;
using Versio.Core.Locking;
using Versio.Core.Hashing;
using Versio.Core.Watching;

namespace Versio.Core.Sync;

public sealed record OrchestratorOptions
{
    /// <summary>Import is detected automatically but applied only on confirmation, because
    /// writing into a .project is the strictly more dangerous direction.</summary>
    public bool RequireImportConfirmation { get; init; } = true;

    public TimeSpan CancelWindow { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int CircuitBreakerThreshold { get; init; } = 5;
    public TimeSpan CircuitBreakerWindow { get; init; } = TimeSpan.FromSeconds(60);
}

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
/// </remarks>
public sealed class SyncOrchestrator
{
    private readonly IClock _clock;
    private readonly ICodesysRunner _codesys;
    private readonly IFileSystem _fs;
    private readonly ILockStore _lockStore;
    private readonly OrchestratorOptions _options;
    private readonly string _repoRoot;
    private readonly LockOwner _owner;
    private readonly IProgress<CodesysProgress>? _progress;

    private readonly Dictionary<string, ProjectBinding> _bindings;
    private readonly Dictionary<string, BindingLock> _locks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobCoalescer> _coalescers = new(StringComparer.Ordinal);
    private readonly CircuitBreaker _breaker;
    private readonly SelfWriteSuppressor _suppressor;

    public SyncOrchestrator(
        string repoRoot,
        IReadOnlyList<ProjectBinding> bindings,
        LockOwner owner,
        IClock clock,
        IFileSystem fs,
        ICodesysRunner codesys,
        ILockStore lockStore,
        OrchestratorOptions? options = null,
        IProgress<CodesysProgress>? progress = null)
    {
        _repoRoot = repoRoot;
        _owner = owner;
        _progress = progress;
        _clock = clock;
        _fs = fs;
        _codesys = codesys;
        _lockStore = lockStore;
        _options = options ?? new OrchestratorOptions();
        _bindings = bindings.ToDictionary(b => b.Id, StringComparer.Ordinal);
        _breaker = new CircuitBreaker(clock, _options.CircuitBreakerThreshold,
                                      _options.CircuitBreakerWindow);
        _suppressor = new SelfWriteSuppressor(clock);

        foreach (var binding in bindings)
        {
            _locks[binding.Id] = new BindingLock { Direction = SyncDirection.Clean };
            _coalescers[binding.Id] =
                new JobCoalescer(clock, _options.CancelWindow, _options.MinimumInterval);
        }
    }

    public SyncDirection StateOf(string bindingId) =>
        _locks.TryGetValue(bindingId, out var l) ? l.Direction : SyncDirection.Clean;

    public bool AnyLockHeld => _locks.Values.Any(l => SyncStateMachine.HoldsLock(l.Direction));

    public CircuitBreaker Breaker => _breaker;

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
        var state = await _lockStore.ReadAsync(ct).ConfigureAwait(false);

        var hashes = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var binding in _bindings.Values)
        {
            hashes[binding.Id] = (await ProjectHashAsync(binding, ct).ConfigureAwait(false),
                                  await ManifestHashAsync(binding, ct).ConfigureAwait(false));
        }

        var decisions = policy.Evaluate(state, hashes);

        foreach (var decision in decisions)
        {
            if (!_locks.ContainsKey(decision.BindingId))
            {
                continue;
            }

            _locks[decision.BindingId] = decision.Outcome switch
            {
                LockRecovery.ReadoptedAfterRestart when state?.Bindings.TryGetValue(
                    decision.BindingId, out var held) == true => held,
                _ => new BindingLock { Direction = SyncDirection.Clean },
            };
        }

        return decisions;
    }

    /// <summary>A change was detected. Decide what, if anything, to do about it.</summary>
    public async Task<SyncOutcome> HandleChangeAsync(SyncJob job, CancellationToken ct)
    {
        if (!_bindings.TryGetValue(job.BindingId, out var binding) || !binding.Enabled)
        {
            return SyncOutcome.Refused(SyncVerdict.UnknownBinding, job.BindingId,
                "no enabled binding with that id");
        }

        var state = StateOf(job.BindingId);

        // 1. Circuit breaker. First, because a tripped breaker means our own reasoning is
        //    no longer trusted and nothing below it should run.
        if (_breaker.IsTripped(job.BindingId))
        {
            return SyncOutcome.Refused(SyncVerdict.BlockedByCircuitBreaker, job.BindingId,
                "sync halted for this project after repeated cycles; acknowledge to resume",
                state);
        }

        // 2. Hash gate. Before the lock on purpose: an event carrying no actual change is
        //    noise, and reporting it as a lock violation would show the user a scary
        //    refusal for a no-op save.
        var (changed, currentHash) = await HasRealChangeAsync(binding, job.Source, ct)
            .ConfigureAwait(false);
        if (!changed)
        {
            return SyncOutcome.Refused(SyncVerdict.NoChange, job.BindingId,
                "content is identical to the last synced state", state);
        }

        // 3. Self-write suppression, for text only: an export writes into the tree the text
        //    watcher is watching. The project file is never written by us.
        if (job.Source == ChangeSource.ExportedText &&
            _suppressor.ShouldSuppress(binding.ExportPath, currentHash))
        {
            return SyncOutcome.Refused(SyncVerdict.SelfWriteSuppressed, job.BindingId,
                "this was the echo of our own export", state);
        }

        // 4. Direction lock.
        var trigger = job.Source == ChangeSource.Project
            ? SyncTrigger.ProjectChanged
            : SyncTrigger.TextChanged;
        var transition = SyncStateMachine.Next(state, trigger);
        if (!transition.Allowed)
        {
            return SyncOutcome.Refused(SyncVerdict.BlockedByLock, job.BindingId,
                transition.Reason, transition.State);
        }

        // 5. Import is detected automatically but applied only on confirmation.
        if (job.Source == ChangeSource.ExportedText &&
            _options.RequireImportConfirmation && !job.Confirmed)
        {
            return SyncOutcome.Refused(SyncVerdict.AwaitingConfirmation, job.BindingId,
                "exported text changed; review the diff and confirm before importing", state);
        }

        // 6. Coalescing. Skipped for a synchronous job: the commit gate must run now and
        //    block, not queue behind a background export.
        if (!job.Synchronous)
        {
            var action = _coalescers[job.BindingId].OnChangeSettled();
            switch (action)
            {
                case CoalesceAction.Coalesced:
                    return SyncOutcome.Refused(SyncVerdict.Coalesced, job.BindingId,
                        "a sync is already running and a follow-up is queued", state);
                case CoalesceAction.Throttled:
                    return SyncOutcome.Refused(SyncVerdict.Throttled, job.BindingId,
                        "throttled; a final sync will run once changes settle", state);
                case CoalesceAction.Queue:
                    return SyncOutcome.Refused(SyncVerdict.Coalesced, job.BindingId,
                        "queued behind the running sync", state);
            }
        }

        if (!_breaker.TryRecord(job.BindingId))
        {
            return SyncOutcome.Refused(SyncVerdict.BlockedByCircuitBreaker, job.BindingId,
                "sync halted for this project after repeated cycles; acknowledge to resume",
                state);
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

        try
        {
            return job.Source == ChangeSource.Project
                ? await ExportAsync(binding, transition, ct).ConfigureAwait(false)
                : await ImportAsync(binding, transition, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!job.Synchronous)
            {
                _coalescers[job.BindingId].OnJobCompleted();
            }
        }
    }

    private async Task<SyncOutcome> ExportAsync(
        ProjectBinding binding, TransitionResult transition, CancellationToken ct)
    {
        var started = _clock.UtcNow;
        var job = new CodesysJob
        {
            Operation = CodesysOperation.Export,
            Profile = binding.Profile ?? string.Empty,
            Projects =
            [
                new ProjectJob
                {
                    BindingId = binding.Id,
                    ProjectPath = Absolute(binding.ProjectPath),
                    Path = Absolute(binding.ExportPath),
                },
            ],
        };

        var result = await _codesys.RunAsync(job, _progress, ct).ConfigureAwait(false);
        var elapsed = _clock.UtcNow - started;

        if (!result.Succeeded)
        {
            await ApplyAsync(binding.Id, SyncTrigger.ExportFailed, string.Empty, ct)
                .ConfigureAwait(false);
            return new SyncOutcome
            {
                Verdict = SyncVerdict.Failed,
                BindingId = binding.Id,
                Explanation = result.FailureReason ?? "the export did not complete",
                State = SyncDirection.Failed,
                Duration = elapsed,
                Warnings = Warnings(result),
            };
        }

        // Register what we wrote so the text watcher's echo is swallowed rather than being
        // mistaken for a user edit.
        var manifestHash = await ManifestHashAsync(binding, ct).ConfigureAwait(false);
        _suppressor.Register(binding.ExportPath, manifestHash);

        var projectHash = await ProjectHashAsync(binding, ct).ConfigureAwait(false);
        await ApplyAsync(binding.Id, SyncTrigger.ExportSucceeded, projectHash, ct)
            .ConfigureAwait(false);

        return new SyncOutcome
        {
            Verdict = SyncVerdict.Completed,
            BindingId = binding.Id,
            Explanation = transition.Reason,
            State = _locks[binding.Id].Direction,
            ChangedFiles = ChangedFiles(result),
            Warnings = Warnings(result),
            Duration = elapsed,
        };
    }

    private async Task<SyncOutcome> ImportAsync(
        ProjectBinding binding, TransitionResult transition, CancellationToken ct)
    {
        var started = _clock.UtcNow;
        var job = new CodesysJob
        {
            Operation = CodesysOperation.Import,
            Profile = binding.Profile ?? string.Empty,
            Projects =
            [
                new ProjectJob
                {
                    BindingId = binding.Id,
                    ProjectPath = Absolute(binding.ProjectPath),
                    Path = Absolute(binding.ExportPath),
                },
            ],
        };

        var result = await _codesys.RunAsync(job, _progress, ct).ConfigureAwait(false);
        var elapsed = _clock.UtcNow - started;

        if (!result.Succeeded)
        {
            await ApplyAsync(binding.Id, SyncTrigger.ImportFailed, string.Empty, ct)
                .ConfigureAwait(false);
            return new SyncOutcome
            {
                Verdict = SyncVerdict.Failed,
                BindingId = binding.Id,
                Explanation = result.FailureReason ?? "the import did not complete",
                State = SyncDirection.Failed,
                Duration = elapsed,
                Warnings = Warnings(result),
            };
        }

        var projectHash = await ProjectHashAsync(binding, ct).ConfigureAwait(false);
        await ApplyAsync(binding.Id, SyncTrigger.ImportSucceeded, projectHash, ct)
            .ConfigureAwait(false);

        return new SyncOutcome
        {
            Verdict = SyncVerdict.Completed,
            BindingId = binding.Id,
            Explanation = transition.Reason,
            State = _locks[binding.Id].Direction,
            Warnings = Warnings(result),
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

        return CommitGate.Evaluate(freshness);
    }

    /// <summary>Run a blocking export for every stale binding, then re-check the gate.</summary>
    public async Task<IReadOnlyList<SyncOutcome>> SatisfyCommitGateAsync(CancellationToken ct)
    {
        var gate = await EvaluateCommitGateAsync(ct).ConfigureAwait(false);
        var outcomes = new List<SyncOutcome>();

        if (gate.Decision != GateDecision.ExportRequired)
        {
            return outcomes;
        }

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
        foreach (var binding in _bindings.Values)
        {
            await ApplyAsync(binding.Id, SyncTrigger.PushSucceeded,
                await ProjectHashAsync(binding, ct).ConfigureAwait(false), ct,
                headCommit).ConfigureAwait(false);
        }
    }

    public Task ReleaseAfterDiscardAsync(string bindingId, CancellationToken ct) =>
        ApplyAsync(bindingId, SyncTrigger.ChangesDiscarded, string.Empty, ct);

    public Task AcknowledgeFailureAsync(string bindingId, CancellationToken ct)
    {
        _breaker.Reset(bindingId);
        _coalescers[bindingId].Reset();
        return ApplyAsync(bindingId, SyncTrigger.FailureAcknowledged, string.Empty, ct);
    }

    private async Task ApplyAsync(string bindingId, SyncTrigger trigger, string projectHash,
                                  CancellationToken ct, string? headCommit = null)
    {
        if (!_locks.TryGetValue(bindingId, out var current))
        {
            return;
        }

        var transition = SyncStateMachine.Next(current.Direction, trigger);
        if (!transition.Allowed)
        {
            return;
        }

        var manifestHash = _bindings.TryGetValue(bindingId, out var binding)
            ? await ManifestHashAsync(binding, ct).ConfigureAwait(false)
            : current.LastSynced.ManifestHash;

        // A successful sync moves LastSynced only. The mirror now reflects the project, but
        // nothing has been published, so the lock still guards real work.
        var synced = trigger is SyncTrigger.ExportSucceeded or SyncTrigger.ImportSucceeded
            ? new SyncedState { ProjectHash = projectHash, ManifestHash = manifestHash }
            : current.LastSynced;

        // A release moves the baseline: this is now the state the world has seen.
        var baseline = transition.State == SyncDirection.Clean
            ? new SyncBaseline
            {
                HeadCommit = headCommit ?? current.Baseline.HeadCommit,
                ProjectHash = projectHash,
                ManifestHash = manifestHash,
            }
            : current.Baseline;

        _locks[bindingId] = current with
        {
            Direction = transition.State,
            AcquiredUtc = transition.State == SyncDirection.Clean ? null : _clock.UtcNow,
            Reason = transition.Reason,
            Baseline = baseline,
            LastSynced = transition.State == SyncDirection.Clean
                ? new SyncedState { ProjectHash = projectHash, ManifestHash = manifestHash }
                : synced,
        };

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

        var manifest = await ManifestHashAsync(binding, ct).ConfigureAwait(false);
        return (manifest != synced.ManifestHash, manifest);
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
    /// Hash of the export tree, taken from its manifest.
    /// </summary>
    /// <remarks>
    /// The manifest already contains a canonical hash per object, so hashing it is
    /// equivalent to hashing the tree and costs one small read instead of walking
    /// megabytes of visualisation XML on every watcher event.
    /// </remarks>
    private async Task<string> ManifestHashAsync(ProjectBinding binding, CancellationToken ct)
    {
        var path = Combine(binding.ExportPath, BindingConventions.ManifestFileName);
        if (!_fs.FileExists(path))
        {
            return string.Empty;
        }

        var text = await _fs.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return ContentHash.Of(text);
    }

    private static IReadOnlyList<string> ChangedFiles(CodesysResult result) =>
        result.Output.Events
            .Where(e => e.Type == "object")
            .Select(e => e.String("path"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

    private static IReadOnlyList<string> Warnings(CodesysResult result) =>
        result.Output.Warnings
            .Select(w => $"{w.String("code")}: {w.String("path")}")
            .ToList();

    private string Absolute(string relative) =>
        Path.IsPathRooted(relative) ? relative : Combine(relative);

    private string Combine(params string[] parts) =>
        Path.Combine([_repoRoot, .. parts.Select(p => p.Replace('/', Path.DirectorySeparatorChar))]);
}
