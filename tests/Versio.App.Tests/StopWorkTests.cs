using Versio.App.ViewModels;
using Versio.Core.Guarding;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The status-bar Stop button is not <see cref="BusyTracker.CancelCancellable"/>.
/// </summary>
public sealed class StopWorkTests
{
    [Fact]
    public void Stop_cancels_import_and_export_tokens()
    {
        using var shell = TestShell.Build();

        using var importing = shell.Busy.Begin(WorkKind.Import, "Importing…");
        using var exporting = shell.Busy.Begin(WorkKind.Export, "Exporting…");

        Assert.True(shell.StopWorkCommand.CanExecute(null));
        shell.StopWorkCommand.Execute(null);

        Assert.True(importing.Token.IsCancellationRequested);
        Assert.True(exporting.Token.IsCancellationRequested);
    }

    [Fact]
    public void Becoming_busy_raises_Stop_can_execute()
    {
        // Mutation check: deleting the RaiseCanExecuteChanged in the IsBusy handler
        // leaves this false. CanExecute() itself always re-evaluates, so the event
        // is what WPF actually listens to.
        using var shell = TestShell.Build();

        var raised = false;
        shell.StopWorkCommand.CanExecuteChanged += (_, _) => raised = true;

        using var exporting = shell.Busy.Begin(WorkKind.Export, "Exporting…");

        Assert.True(raised);
        Assert.True(shell.StopWorkCommand.CanExecute(null));
    }

    [Fact]
    public void Retry_is_the_existing_command_and_stays_off_until_a_failure()
    {
        using var shell = TestShell.Build();

        Assert.False(shell.Sync.HasFailure);
        Assert.False(shell.Sync.RetryCommand.CanExecute(null));
    }
}
