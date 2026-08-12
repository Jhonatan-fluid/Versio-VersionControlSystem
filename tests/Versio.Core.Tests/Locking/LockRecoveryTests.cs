using Versio.Core.Locking;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Locking;

/// <summary>
/// Lock survival across app close and machine reboot. The load-bearing rule: an abandoned
/// lock is <b>never auto-cleared</b>, because the unsynced work it guards is still unsynced
/// after a reboot.
/// </summary>
public class LockRecoveryTests
{
    private static readonly DateTimeOffset Boot = new(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);

    private sealed class Machine(DateTimeOffset boot, int pid = 1000) : IMachineIdentity
    {
        public string MachineName => "DESKTOP-ABC";
        public string UserName => "elli_";
        public int CurrentProcessId => pid;
        public DateTimeOffset BootId => boot;
    }

    private sealed class Probe(bool alive) : IProcessProbe
    {
        public bool IsVersioProcessAlive(int processId, string sessionId) => alive;

    public bool IsProcessAlive(int processId) => alive;
    }

    private static LockState StateWith(SyncDirection direction, DateTimeOffset boot,
                                       string projectHash = "sha256:p", string mirrorHash = "sha256:m")
        => new()
        {
            RepoPath = @"C:\repo",
            Owner = new LockOwner
            {
                Machine = "DESKTOP-ABC",
                User = "elli_",
                SessionId = "session-1",
                ProcessId = 21884,
                BootId = boot,
            },
            Bindings = new Dictionary<string, BindingLock>
            {
                ["b-1"] = new()
                {
                    Direction = direction,
                    Reason = "saved",
                    Baseline = new SyncBaseline
                    {
                        HeadCommit = "9291d00",
                        ProjectHash = projectHash,
                        MirrorHash = mirrorHash,
                    },
                },
            },
        };

    [Fact]
    public void A_live_owning_session_is_never_stolen_from()
    {
        var policy = new LockRecoveryPolicy(new Machine(Boot), new Probe(alive: true));

        var result = policy.Evaluate(StateWith(SyncDirection.ProjectAuthoritative, Boot),
            new Dictionary<string, (string, string)> { ["b-1"] = ("sha256:changed", "sha256:m") });

        Assert.Equal(LockRecovery.HeldByLiveSession, result.Single().Outcome);
    }

    [Fact]
    public void After_a_reboot_an_unsynced_lock_is_re_adopted_not_cleared()
    {
        // Different bootId proves the owning process is definitively gone. But the work is
        // still unsynced, so the lock must survive.
        var laterBoot = Boot.AddDays(1);
        var policy = new LockRecoveryPolicy(new Machine(laterBoot), new Probe(alive: false));

        var result = policy.Evaluate(StateWith(SyncDirection.ProjectAuthoritative, Boot),
            new Dictionary<string, (string, string)> { ["b-1"] = ("sha256:changed", "sha256:m") });

        var decision = result.Single();
        Assert.Equal(LockRecovery.ReadoptedAfterRestart, decision.Outcome);
        Assert.Contains("rebooted", decision.Explanation);
    }

    [Fact]
    public void After_a_crash_on_the_same_boot_an_unsynced_lock_is_re_adopted()
    {
        var policy = new LockRecoveryPolicy(new Machine(Boot), new Probe(alive: false));

        var result = policy.Evaluate(StateWith(SyncDirection.TextAuthoritative, Boot),
            new Dictionary<string, (string, string)> { ["b-1"] = ("sha256:p", "sha256:changed") });

        var decision = result.Single();
        Assert.Equal(LockRecovery.ReadoptedAfterRestart, decision.Outcome);
        Assert.Contains("is gone", decision.Explanation);
    }

    [Fact]
    public void A_lock_whose_work_was_reverted_externally_is_released()
    {
        // Content is back at the recorded baseline, so there is nothing left to guard.
        var policy = new LockRecoveryPolicy(new Machine(Boot.AddDays(1)), new Probe(alive: false));

        var result = policy.Evaluate(StateWith(SyncDirection.ProjectAuthoritative, Boot),
            new Dictionary<string, (string, string)> { ["b-1"] = ("sha256:p", "sha256:m") });

        Assert.Equal(LockRecovery.ReleasedStaleLock, result.Single().Outcome);
    }

    [Fact]
    public void A_clean_binding_needs_no_recovery()
    {
        var policy = new LockRecoveryPolicy(new Machine(Boot), new Probe(alive: false));

        var result = policy.Evaluate(StateWith(SyncDirection.Clean, Boot),
            new Dictionary<string, (string, string)> { ["b-1"] = ("sha256:p", "sha256:m") });

        Assert.Equal(LockRecovery.NothingToRecover, result.Single().Outcome);
    }

    [Fact]
    public void Missing_hashes_are_treated_as_still_differing()
    {
        // Fail safe: if we cannot prove the work was completed, keep the lock.
        var policy = new LockRecoveryPolicy(new Machine(Boot.AddDays(1)), new Probe(alive: false));

        var result = policy.Evaluate(StateWith(SyncDirection.ProjectAuthoritative, Boot),
            new Dictionary<string, (string, string)>());

        Assert.Equal(LockRecovery.ReadoptedAfterRestart, result.Single().Outcome);
    }

    [Fact]
    public void No_previous_state_yields_no_decisions()
    {
        var policy = new LockRecoveryPolicy(new Machine(Boot), new Probe(alive: false));

        Assert.Empty(policy.Evaluate(null, new Dictionary<string, (string, string)>()));
    }

    [Fact]
    public void Each_binding_is_recovered_independently()
    {
        // Locks are per binding: project A can be authoritative while B is clean.
        var state = new LockState
        {
            Owner = new LockOwner
            {
                Machine = "DESKTOP-ABC", User = "elli_", SessionId = "s",
                ProcessId = 1, BootId = Boot,
            },
            Bindings = new Dictionary<string, BindingLock>
            {
                ["b-1"] = new()
                {
                    Direction = SyncDirection.ProjectAuthoritative,
                    Baseline = new SyncBaseline { ProjectHash = "p1", MirrorHash = "m1" },
                },
                ["b-2"] = new()
                {
                    Direction = SyncDirection.TextAuthoritative,
                    Baseline = new SyncBaseline { ProjectHash = "p2", MirrorHash = "m2" },
                },
                ["b-3"] = new() { Direction = SyncDirection.Clean },
            },
        };

        var policy = new LockRecoveryPolicy(new Machine(Boot.AddDays(1)), new Probe(alive: false));
        var result = policy.Evaluate(state, new Dictionary<string, (string, string)>
        {
            ["b-1"] = ("p1", "m1"),          // reverted -> release
            ["b-2"] = ("p2", "CHANGED"),     // still dirty -> re-adopt
        });

        Assert.Equal(LockRecovery.ReleasedStaleLock, result.Single(r => r.BindingId == "b-1").Outcome);
        Assert.Equal(LockRecovery.ReadoptedAfterRestart, result.Single(r => r.BindingId == "b-2").Outcome);
        Assert.Equal(LockRecovery.NothingToRecover, result.Single(r => r.BindingId == "b-3").Outcome);
    }

    [Fact]
    public void A_lock_from_a_different_machine_is_not_treated_as_ours()
    {
        var state = StateWith(SyncDirection.ProjectAuthoritative, Boot) with
        {
            Owner = new LockOwner
            {
                Machine = "OTHER-PC", User = "someone", SessionId = "s",
                ProcessId = 5, BootId = Boot,
            },
        };

        var policy = new LockRecoveryPolicy(new Machine(Boot), new Probe(alive: true));
        var result = policy.Evaluate(state,
            new Dictionary<string, (string, string)> { ["b-1"] = ("sha256:changed", "sha256:m") });

        // Same boot time on a different machine is a coincidence, not proof of liveness.
        Assert.Equal(LockRecovery.ReadoptedAfterRestart, result.Single().Outcome);
    }
}
