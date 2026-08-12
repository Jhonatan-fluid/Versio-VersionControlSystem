namespace Versio.Core.Locking;

/// <summary>Tells the recovery logic whether a given process is still alive.</summary>
public interface IProcessProbe
{
    /// <summary>True when a Versio process with this id is currently running.</summary>
    bool IsVersioProcessAlive(int processId, string sessionId);
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
    /// <param name="currentHashes">Freshly computed (projectHash, manifestHash) per binding.</param>
    public IReadOnlyList<RecoveryDecision> Evaluate(
        LockState? state,
        IReadOnlyDictionary<string, (string ProjectHash, string ManifestHash)> currentHashes)
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
                    $"held by a running Versio session (pid {owner.ProcessId}); opening read-only"));
                continue;
            }

            // Owner is gone. Was the work actually completed or reverted while we were away?
            var stillDiffers = !currentHashes.TryGetValue(bindingId, out var now) ||
                               now.ProjectHash != bindingLock.Baseline.ProjectHash ||
                               now.ManifestHash != bindingLock.Baseline.ManifestHash;

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
