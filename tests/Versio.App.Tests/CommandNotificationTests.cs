using Versio.App.Mvvm;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A button whose enabled state changes is told about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these pin.</b> <see cref="AsyncCommand"/> raises a plain event and does not
/// hook <c>CommandManager.RequerySuggested</c>, deliberately — WPF's global requery fires on
/// focus and mouse events and would re-evaluate every command in the window constantly. The
/// consequence is that a command is re-evaluated **only** when something calls
/// <c>RaiseCanExecuteChanged</c> on it, so a command missing from the sweep is a button that
/// keeps whatever enabled state it had when it was last asked.
/// </para>
/// <para>
/// Four were missing from <c>RaiseGitCommandStates</c> and were instead being raised from the
/// setter of <c>AuthState</c> — mis-indented, and reachable only when credentials changed.
/// Nothing any of them depends on has anything to do with credentials:
/// <c>AbortMergeCommand</c> is <c>Ready &amp;&amp; IsMidMerge</c>, <c>ReconcileCommand</c> is
/// <c>Ready &amp;&amp; Standing.State == Diverged</c>, <c>DeleteBranchesCommand</c> is
/// <c>Ready &amp;&amp; Branches.Count &gt; 1 &amp;&amp; DefaultBranch.IsKnown</c>.
/// </para>
/// <para>
/// So a refresh that discovered a merge in progress raised <c>IsMidMerge</c>, called the sweep,
/// and left <i>Abandon merge</i> exactly as disabled as it had been — until something unrelated
/// happened to the credentials.
/// </para>
/// <para>
/// <b>Asserted on the notification, not on the predicate.</b> <c>CanExecute</c> was always
/// right; asking it directly would have passed throughout. What was missing is the telling.
/// </para>
/// </remarks>
public sealed class CommandNotificationTests
{
    /// <summary>Every command the git sweep is responsible for, and what it depends on.</summary>
    /// <remarks>
    /// A theory rather than one assertion per command, so a command added to the panel and not
    /// to the sweep fails here by being added to this list — which is the one place somebody
    /// writing a new button will look.
    /// </remarks>
    public static TheoryData<string> SweptCommands =>
    [
        nameof(ShellViewModel.AbortMergeCommand),
        nameof(ShellViewModel.ReconcileCommand),
        nameof(ShellViewModel.DeleteBranchesCommand),
        nameof(ShellViewModel.ResolveCommand),
        nameof(ShellViewModel.CommitCommand),
        nameof(ShellViewModel.PullCommand),
        nameof(ShellViewModel.PushCommand),
        nameof(ShellViewModel.FetchCommand),
    ];

    [Theory]
    [MemberData(nameof(SweptCommands))]
    public void A_command_is_told_when_the_shell_sweeps_its_states(string command)
    {
        using var shell = TestShell.Build();

        var raised = 0;
        Subscribe(shell, command, () => raised++);

        // Driven through the repository reset, which is one of the seven places that call the
        // sweep and the only one reachable without a repository. Which caller is used does not
        // matter: what is under test is whether the sweep reaches this command at all.
        shell.ResetForRepositoryChange(null, @"C:\somewhere");

        Assert.True(raised > 0,
                    $"{command} was never told its state may have changed, so the button keeps " +
                    "whatever enabled state it last had");
    }

    private static void Subscribe(ShellViewModel shell, string command, Action onRaised)
    {
        var value = typeof(ShellViewModel).GetProperty(command)!.GetValue(shell)!;
        var handler = new EventHandler((_, _) => onRaised());

        value.GetType().GetEvent(nameof(AsyncCommand.CanExecuteChanged))!
            .AddEventHandler(value, handler);
    }
}
