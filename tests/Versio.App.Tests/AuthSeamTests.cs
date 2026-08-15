using System.ComponentModel;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The shell still hears the credential collaborator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists.</b> Auth and identity used to live inside <c>ShellViewModel</c> and
/// called <c>RaiseHealth</c> and <c>RaiseGitCommandStates</c> directly. Moving them out replaced
/// those two calls with one <c>Changed</c> event and one subscription — and a subscription is
/// exactly the kind of seam that nothing notices when it goes.
/// </para>
/// <para>
/// That is not a hypothesis. <b>Deleting the subscription left all 467 tests green</b>, which is
/// the same result an earlier refactor got when it moved three panel handlers and nothing
/// crossed the seam either. So these two are the tests that had to exist before the extraction
/// could be believed, and they are written against the <i>consequences</i> the shell owns rather
/// than against the event, because an assertion on the event would pass while the shell ignored
/// it.
/// </para>
/// <para>
/// <b>Driven through <c>NoteAuthFailure</c>, which needs no repository.</b> It is the path every
/// failed remote operation takes, it is public because the shell calls it from those paths, and
/// it moves <c>AuthState</c> without a git client, a temp repository or a network. The identity
/// half of the same seam is covered by <see cref="GitHealthTests"/> against real git.
/// </para>
/// </remarks>
public sealed class AuthSeamTests
{
    /// <summary>
    /// A credential failure reaches the status light.
    /// </summary>
    /// <remarks>
    /// <c>Health</c> is computed from <c>ShellConditions</c>, one of which is
    /// <c>AuthNeedsAttention</c>. It is a computed property with no setter of its own, so the
    /// only thing that can make it move is somebody raising it — and after the extraction the
    /// only thing that can is the subscription.
    /// </remarks>
    [Fact]
    public void A_credential_failure_moves_the_status_light()
    {
        using var shell = TestShell.Build();

        var raised = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.Auth.NoteAuthFailure(Rejected);

        Assert.Equal(GitAuthState.Rejected, shell.Auth.AuthState);
        Assert.Contains(nameof(ShellViewModel.Health), raised);
    }

    /// <summary>
    /// A credential failure re-asks the git buttons whether they can run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AsyncCommand</c> raises a plain event and deliberately does not hook
    /// <c>CommandManager.RequerySuggested</c> — WPF's global requery fires on focus and mouse
    /// events and would re-evaluate every command in the window constantly. The price is that a
    /// command nothing sweeps is never re-evaluated at all.
    /// </para>
    /// <para>
    /// <c>CommitCommand</c> is the one asserted on because it is the one with a real dependency
    /// on this collaborator: <c>CanCommit</c> gates on <c>Auth.Identity.IsComplete</c>, which is
    /// what stops a 17–31 second CODESYS export running for a commit git would refuse.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_credential_failure_re_asks_the_commit_button()
    {
        using var shell = TestShell.Build();

        var raised = 0;
        shell.CommitCommand.CanExecuteChanged += (_, _) => raised++;

        shell.Auth.NoteAuthFailure(Rejected);

        Assert.True(raised > 0,
                    "the shell never re-asked CommitCommand after the credential state moved, " +
                    "so the button keeps whatever enabled state it last had");
    }

    /// <summary>
    /// The remote the shell reads is the one the collaborator parsed.
    /// </summary>
    /// <remarks>
    /// Hazard the extraction was warned about: <c>_remote</c> was read by three unrelated
    /// things — the credential, forge detection and the merge-request button. It now has one
    /// owner and the shell asks for it, so this pins that the shell is reading the live value
    /// rather than a copy it took once.
    /// </remarks>
    [Fact]
    public void The_shell_reads_the_remote_the_collaborator_owns()
    {
        using var shell = TestShell.Build();

        Assert.Equal(RemoteUrl.None.Host, shell.Auth.Remote.Host);

        shell.Auth.SetRemote(RemoteUrl.Parse("https://github.com/acme/widgets.git"));

        Assert.Equal("github.com", shell.Auth.Remote.Host);
        Assert.Equal("github.com", shell.Auth.RemoteHost);
    }

    /// <summary>An HTTPS rejection: the ordinary "your credential was refused".</summary>
    private static GitResult Rejected => new()
    {
        Success = false,
        ExitCode = 128,
        StdErr = "fatal: Authentication failed for 'https://github.com/acme/widgets.git/'",
    };
}
