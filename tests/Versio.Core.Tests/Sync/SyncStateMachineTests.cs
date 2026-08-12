using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// Every legal transition, and — more importantly — every illegal one. The blocked cases
/// are the product feature: they are what stops a bi-directional sync from looping or
/// overwriting somebody's work.
/// </summary>
public class SyncStateMachineTests
{
    [Fact]
    public void Clean_project_change_takes_the_project_direction()
    {
        var r = SyncStateMachine.Next(SyncDirection.Clean, SyncTrigger.ProjectChanged);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.ProjectAuthoritative, r.State);
    }

    [Fact]
    public void Clean_text_change_takes_the_text_direction()
    {
        var r = SyncStateMachine.Next(SyncDirection.Clean, SyncTrigger.TextChanged);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.TextAuthoritative, r.State);
    }

    [Fact]
    public void Text_edits_are_BLOCKED_while_the_project_is_authoritative()
    {
        // This is the lock. Without it, export -> watcher -> import -> export loops.
        var r = SyncStateMachine.Next(SyncDirection.ProjectAuthoritative, SyncTrigger.TextChanged);

        Assert.False(r.Allowed);
        Assert.Equal(SyncDirection.ProjectAuthoritative, r.State);
        Assert.Contains("push or discard", r.Reason);
    }

    [Fact]
    public void Project_edits_are_BLOCKED_while_the_text_is_authoritative()
    {
        var r = SyncStateMachine.Next(SyncDirection.TextAuthoritative, SyncTrigger.ProjectChanged);

        Assert.False(r.Allowed);
        Assert.Equal(SyncDirection.TextAuthoritative, r.State);
    }

    [Fact]
    public void Further_project_edits_are_allowed_once_the_project_direction_is_held()
    {
        var r = SyncStateMachine.Next(SyncDirection.ProjectAuthoritative, SyncTrigger.ProjectChanged);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.ProjectAuthoritative, r.State);
    }

    [Theory]
    [InlineData(SyncDirection.ProjectAuthoritative)]
    [InlineData(SyncDirection.TextAuthoritative)]
    public void Push_releases_the_lock(SyncDirection held)
    {
        var r = SyncStateMachine.Next(held, SyncTrigger.PushSucceeded);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.Clean, r.State);
    }

    [Theory]
    [InlineData(SyncDirection.ProjectAuthoritative)]
    [InlineData(SyncDirection.TextAuthoritative)]
    public void Discard_releases_the_lock(SyncDirection held)
    {
        var r = SyncStateMachine.Next(held, SyncTrigger.ChangesDiscarded);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.Clean, r.State);
    }

    [Fact]
    public void Export_success_does_NOT_release_the_lock()
    {
        // Exporting is not publishing. Until the work is pushed nobody else can see it,
        // so the direction stays owned.
        var r = SyncStateMachine.Next(SyncDirection.ProjectAuthoritative, SyncTrigger.ExportSucceeded);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.ProjectAuthoritative, r.State);
    }

    [Fact]
    public void Failure_is_sticky_until_acknowledged()
    {
        var failed = SyncStateMachine.Next(SyncDirection.ProjectAuthoritative, SyncTrigger.ExportFailed);
        Assert.Equal(SyncDirection.Failed, failed.State);

        // Silently resuming after a failure is how half-written state becomes permanent.
        var blocked = SyncStateMachine.Next(SyncDirection.Failed, SyncTrigger.ProjectChanged);
        Assert.False(blocked.Allowed);
        Assert.Equal(SyncDirection.Failed, blocked.State);

        var cleared = SyncStateMachine.Next(SyncDirection.Failed, SyncTrigger.FailureAcknowledged);
        Assert.True(cleared.Allowed);
        Assert.Equal(SyncDirection.Clean, cleared.State);
    }

    [Fact]
    public void Conflict_blocks_everything_until_resolved()
    {
        var blocked = SyncStateMachine.Next(SyncDirection.Conflict, SyncTrigger.ProjectChanged);
        Assert.False(blocked.Allowed);

        var resolved = SyncStateMachine.Next(SyncDirection.Conflict, SyncTrigger.ConflictResolved);
        Assert.True(resolved.Allowed);
        Assert.Equal(SyncDirection.Clean, resolved.State);
    }

    [Theory]
    [InlineData(SyncDirection.ProjectAuthoritative)]
    [InlineData(SyncDirection.TextAuthoritative)]
    [InlineData(SyncDirection.Conflict)]
    [InlineData(SyncDirection.Failed)]
    public void Force_release_always_works(SyncDirection from)
    {
        var r = SyncStateMachine.Next(from, SyncTrigger.ForceReleased);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.Clean, r.State);
    }

    [Fact]
    public void Push_while_already_clean_is_a_no_op_not_an_error()
    {
        var r = SyncStateMachine.Next(SyncDirection.Clean, SyncTrigger.PushSucceeded);

        Assert.True(r.Allowed);
        Assert.Equal(SyncDirection.Clean, r.State);
    }

    [Theory]
    [InlineData(SyncDirection.Clean, true, true)]
    [InlineData(SyncDirection.ProjectAuthoritative, true, false)]
    [InlineData(SyncDirection.TextAuthoritative, false, true)]
    [InlineData(SyncDirection.Conflict, false, false)]
    [InlineData(SyncDirection.Failed, false, false)]
    public void Permission_helpers_agree_with_the_transitions(
        SyncDirection state, bool exportAllowed, bool importAllowed)
    {
        Assert.Equal(exportAllowed, SyncStateMachine.ExportAllowed(state));
        Assert.Equal(importAllowed, SyncStateMachine.ImportAllowed(state));
    }

    [Theory]
    [InlineData(SyncDirection.Clean, false)]
    [InlineData(SyncDirection.ProjectAuthoritative, true)]
    [InlineData(SyncDirection.TextAuthoritative, true)]
    [InlineData(SyncDirection.Conflict, true)]
    [InlineData(SyncDirection.Failed, true)]
    public void HoldsLock_gates_branch_switching(SyncDirection state, bool holds)
    {
        Assert.Equal(holds, SyncStateMachine.HoldsLock(state));
    }

    [Fact]
    public void No_trigger_can_reach_an_undefined_state()
    {
        // Exhaustive sweep: the machine must always return a defined state, never throw.
        foreach (var state in Enum.GetValues<SyncDirection>())
        {
            foreach (var trigger in Enum.GetValues<SyncTrigger>())
            {
                var r = SyncStateMachine.Next(state, trigger);
                Assert.True(Enum.IsDefined(r.State));
                Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            }
        }
    }

    [Fact]
    public void A_blocked_transition_never_changes_state()
    {
        foreach (var state in Enum.GetValues<SyncDirection>())
        {
            foreach (var trigger in Enum.GetValues<SyncTrigger>())
            {
                var r = SyncStateMachine.Next(state, trigger);
                if (!r.Allowed)
                {
                    Assert.Equal(state, r.State);
                }
            }
        }
    }
}
