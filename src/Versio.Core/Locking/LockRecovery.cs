namespace Versio.Core.Locking;

/// <summary>Tells the recovery logic whether a given process is still alive.</summary>
public interface IProcessProbe
{
    /// <summary>True when a Versio process with this id is currently running.</summary>
    bool IsVersioProcessAlive(int processId, string sessionId);

    /// <summary>True when any process with this id is running, whatever it is.</summary>
    /// <remarks>
    /// The name is not checked, because the caller is asking about somebody else's process —
    /// a vendor IDE holding a project open. Pid reuse makes this an over-estimate, and that is
    /// the right way to be wrong: believing a lock that has expired costs a wait, and
    /// disbelieving one that has not costs two writers on one project.
    /// </remarks>
    bool IsProcessAlive(int processId);

    /// <summary>True when any process of this name (no <c>.exe</c>) is running on this machine.</summary>
    /// <remarks>
    /// <para>
    /// For a vendor whose open-project marker carries no pid. TIA Portal's <c>.info</c> names
    /// the user, the machine and the time, and nothing else; a dead pid is not a question it
    /// can be asked. What it <i>can</i> be asked, once it names this machine, is whether the
    /// IDE that writes it is running here at all — a Portal that is not running holds nothing,
    /// whatever its leftover says. The process names are the vendor's
    /// (<see cref="Plc.IPlcProjectFormat.HolderProcessNames"/>); this is only the lookup.
    /// </para>
    /// <para>
    /// The default answers <c>true</c>: a probe that cannot look is a probe that believes the
    /// marker, which is the safe way to be wrong, and the way every fake in the suite was
    /// already behaving before the member existed.
    /// </para>
    /// </remarks>
    bool IsAnyProcessRunning(string processName) => true;
}

/// <summary>Current machine identity. Abstracted so recovery is unit testable.</summary>
public interface IMachineIdentity
{
    string MachineName { get; }
    string UserName { get; }
    int CurrentProcessId { get; }

    /// <summary>Machine boot time. Changes on every reboot.</summary>
    DateTimeOffset BootId { get; }
}

/// <summary>What recovery decided to do about one persisted lock, and why.</summary>
public readonly record struct RecoveryDecision(
    LockRecovery Outcome,
    string BindingId,
    string Explanation);

/// <summary>
/// Decides what to do with a lock found on disk at startup.
/// </summary>
/// <remarks>
/// The important design point: <b>an abandoned lock is never auto-cleared.</b> The lock
/// exists because there is unsynced work, and that work is still unsynced after a reboot.
/// Recovery therefore re-validates against content hashes rather than trusting elapsed
/// time — the same content-derived reasoning that makes the hash gate reliable.
/// </remarks>
public sealed class LockRecoveryPolicy(IMachineIdentity machine, IProcessProbe processes)
{
    /// <param name="state">What was read from disk.</param>
    /// <param name="currentHashes">Freshly computed (projectHash, mirrorHash) per binding.</param>
    public IReadOnlyList<RecoveryDecision> Evaluate(
        LockState? state,
        IReadOnlyDictionary<string, (string ProjectHash, string MirrorHash)> currentHashes)
    {
        var results = new List<RecoveryDecision>();
        if (state?.Owner is null || state.Bindings.Count == 0)
        {
            return results;
        }

        var owner = state.Owner;
        var sameMachine = string.Equals(owner.Machine, machine.MachineName, StringComparison.OrdinalIgnoreCase);
        var sameBoot = owner.BootId == machine.BootId;
        var ownerAlive = sameMachine && sameBoot &&
                         processes.IsVersioProcessAlive(owner.ProcessId, owner.SessionId);

        foreach (var (bindingId, bindingLock) in state.Bindings)
        {
            if (!Sync.SyncStateMachine.HoldsLock(bindingLock.Direction))
            {
                results.Add(new RecoveryDecision(LockRecovery.NothingToRecover, bindingId,
                    "binding was clean"));
                continue;
            }

            if (ownerAlive)
            {
                results.Add(new RecoveryDecision(LockRecovery.HeldByLiveSession, bindingId,
                    $"held by a running Versio session (pid {owner.ProcessId}); leaving it alone"));
                continue;
            }

            // Owner is gone. Was the work actually completed or reverted while we were away?
            var stillDiffers = !currentHashes.TryGetValue(bindingId, out var now) ||
                               now.ProjectHash != bindingLock.Baseline.ProjectHash ||
                               now.MirrorHash != bindingLock.Baseline.MirrorHash;

            if (stillDiffers)
            {
                var why = sameBoot
                    ? $"previous session (pid {owner.ProcessId}) is gone"
                    : "the machine rebooted";
                results.Add(new RecoveryDecision(LockRecovery.ReadoptedAfterRestart, bindingId,
                    $"{why} and the work is still unsynced; re-adopting the {bindingLock.Direction} lock"));
            }
            else
            {
                results.Add(new RecoveryDecision(LockRecovery.ReleasedStaleLock, bindingId,
                    "content is back at the recorded baseline; releasing the stale lock"));
            }
        }

        return results;
    }
}
