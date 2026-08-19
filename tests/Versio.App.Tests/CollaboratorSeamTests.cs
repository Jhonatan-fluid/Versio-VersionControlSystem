using System.ComponentModel;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The shell still hears every collaborator that was lifted out of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each extraction replaces two direct calls with one event and one subscription.</b> Auth
/// and identity, then the remote surface, both used to call <c>RaiseHealth</c> and
/// <c>RaiseGitCommandStates</c> from inside the shell; a collaborator cannot, so each raises a
/// <c>Changed</c> event that the shell subscribes to. That subscription is the entire seam, and
/// it is one line.
/// </para>
/// <para>
/// <b>Deleting it left the whole suite green. Twice.</b> Once for auth (467 tests), once for the
/// remote surface (475). That is not bad luck — it is what a pure move looks like to a test
/// suite: nothing about it resembles new behaviour, so nothing was ever written to watch it. The
/// same shape has now been seen four times in this repository, counting the three panel handlers
/// that crossed a <c>DataContext</c> seam and the four commands raised from the wrong setter.
/// </para>
/// <para>
/// So this is a <b>theory over collaborators</b> rather than a class per extraction. Staging and
/// the repository session are next out of that file; each should arrive here as one more row,
/// which is a great deal likelier to happen than each remembering to write its own class.
/// </para>
/// <para>
/// <b>Asserted on the consequences the shell owns</b>, never on the event. A test that watched
/// <c>Changed</c> fire would pass happily while the shell ignored it, which is precisely the
/// defect.
/// </para>
/// </remarks>
public sealed class CollaboratorSeamTests
{
    /// <summary>The collaborators, and how to make each one move.</summary>
    public static TheoryData<string> Collaborators =>
    [
        nameof(ShellViewModel.Auth),
        nameof(ShellViewModel.Remote),
    ];

    /// <summary>
    /// Something the collaborator owns moved, so the status light was told.
    /// </summary>
    /// <remarks>
    /// Explorer's seam is the pane, not Health — see
    /// <see cref="ExplorerFlowTests.An_explorer_selection_reaches_the_pane"/>. Deleting that
    /// subscription is the mutation check; this theory does not cover it.
    /// </remarks>
    /// <remarks>
    /// <c>Health</c> is computed from <c>ShellConditions</c> and has no setter of its own, so
    /// the only thing that can raise it is the subscription.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Collaborators))]
    public void A_collaborator_that_moves_reaches_the_status_light(string collaborator)
    {
        using var shell = TestShell.Build();

        var raised = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Nudge(shell, collaborator);

        Assert.Contains(nameof(ShellViewModel.Health), raised);
    }

    /// <summary>
    /// Something the collaborator owns moved, so the git buttons were re-asked.
    /// </summary>
    /// <remarks>
    /// <c>AsyncCommand</c> raises a plain event and deliberately does not hook
    /// <c>CommandManager.RequerySuggested</c> — WPF's global requery fires on focus and mouse
    /// events and would re-evaluate every command in the window constantly. The price is that a
    /// command nothing sweeps is never re-evaluated at all, so a button keeps whatever state it
    /// last had.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Collaborators))]
    public void A_collaborator_that_moves_re_asks_the_commit_button(string collaborator)
    {
        using var shell = TestShell.Build();

        var raised = 0;
        shell.CommitCommand.CanExecuteChanged += (_, _) => raised++;

        Nudge(shell, collaborator);

        Assert.True(raised > 0,
                    $"the shell never re-asked CommitCommand after {collaborator} moved, so the " +
                    "button keeps whatever enabled state it last had");
    }

    /// <summary>
    /// The remote the shell reads is the one the collaborator parsed.
    /// </summary>
    /// <remarks>
    /// The hazard the auth extraction was warned about: <c>_remote</c> was read by three
    /// unrelated things — the credential, forge detection and the merge-request button. It has
    /// one owner now and the shell asks for it, so this pins that the shell reads the live value
    /// rather than a copy it took at open.
    /// </remarks>
    [Fact]
    public void The_shell_reads_the_remote_the_credential_collaborator_owns()
    {
        using var shell = TestShell.Build();

        Assert.Equal(RemoteUrl.None.Host, shell.Auth.Remote.Host);

        shell.Auth.SetRemote(RemoteUrl.Parse("https://github.com/acme/widgets.git"));

        Assert.Equal("github.com", shell.Auth.Remote.Host);
        Assert.Equal("github.com", shell.Auth.RemoteHost);
    }

    /// <summary>Move one thing the collaborator owns, through its own public surface.</summary>
    /// <remarks>
    /// Both nudges work with no repository open, no git client and no network, which is what
    /// keeps this class cheap enough that adding a row to it is never a reason not to.
    /// </remarks>
    private static void Nudge(ShellViewModel shell, string collaborator)
    {
        switch (collaborator)
        {
            case nameof(ShellViewModel.Auth):
                // The path every failed remote operation takes.
                shell.Auth.NoteAuthFailure(new GitResult
                {
                    Success = false,
                    ExitCode = 128,
                    StdErr = "fatal: Authentication failed for " +
                             "'https://github.com/acme/widgets.git/'",
                });
                break;

            case nameof(ShellViewModel.Remote):
                // What every refresh does once git has reported a status.
                shell.Remote.NoteStatus(new BranchInfo
                {
                    Name = "main",
                    Upstream = "origin/main",
                    Ahead = 2,
                });
                break;

            default:
                Assert.Fail($"{collaborator} has no nudge - add one when you add the row");
                break;
        }
    }
}
