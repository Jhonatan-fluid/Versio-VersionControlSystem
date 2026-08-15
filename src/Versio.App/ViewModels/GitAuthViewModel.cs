using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Logging;

namespace Versio.App.ViewModels;

/// <summary>
/// What stands between this machine and the remote: the credential, and who git will sign a
/// commit as.
/// </summary>
/// <remarks>
/// <para>
/// The first collaborator lifted out of <c>ShellViewModel</c>, chosen because it had the fewest
/// ties into that class's private fields — not because it is the largest cluster. Auth and
/// identity travel together because they are one question asked twice: <b>can this machine
/// speak to the remote, and does git know who is asking.</b> They share a panel, and they are
/// the two things that stop a push for reasons that have nothing to do with the code.
/// </para>
/// <para>
/// <b>Versio implements no login, and this class is the reason that stays true.</b> Everything
/// here is detect, explain, hand off — <c>git credential fill</c>, never a named helper, never a
/// username box. Driving <c>git.exe</c> is what makes GCM, SSH agents, 2FA and corporate
/// proxies behave exactly as they do in the engineer's own terminal, and storing a token would
/// mean owning a secret store, a rotation story and a breach story.
/// </para>
/// <para>
/// <b>NOTHING IN HERE MAY LOG WHAT IT SENDS OR RECEIVES.</b> <c>git credential</c> speaks
/// key=value over stdin and one of those keys is <c>password</c>, which <c>Redact</c> does not
/// catch. The results carry whether an operation worked and never the helper's stdout. Read
/// <c>docs/GIT-PANEL-PLAN.md</c> §12c before changing a log line in this file.
/// </para>
/// <para>
/// <b>The four collaborators are passed in rather than reached for.</b> The busy tracker and the
/// notification centre are the shell's because one progress bar and one notification list are
/// the point of having them; <c>ready</c> is the shell's own gate (<c>a client, and nothing
/// running</c>) and is a delegate rather than a copied predicate so the two can never disagree;
/// and errors go to the shell's reporter so a failure here surfaces the same way as a failed
/// push.
/// </para>
/// </remarks>
public sealed class GitAuthViewModel : ObservableObject
{
    private readonly VersioLogger _logger;
    private readonly BusyTracker _busy;
    private readonly NotificationCenter _notifications;
    private readonly Func<bool> _ready;

    private IGitClient? _git;
    private RemoteUrl _remote = RemoteUrl.None;
    private GitAuthState _authState = GitAuthState.Fine;
    private GitIdentity _identity = GitIdentity.None;
    private string _identityName = string.Empty;
    private string _identityEmail = string.Empty;
    private bool _identityGlobal;

    public GitAuthViewModel(
        VersioLogger logger,
        BusyTracker busy,
        NotificationCenter notifications,
        Func<bool> ready,
        Action<Exception> reportError)
    {
        _logger = logger;
        _busy = busy;
        _notifications = notifications;
        _ready = ready;

        SignInCommand = new AsyncCommand(SignInAsync, () => _ready() && CanSignIn, reportError);

        // Available whenever a credential could be stored, not only when one has failed:
        // "sign out of this host" is a thing somebody may want to do deliberately, and a
        // button that only appears after a failure cannot be found before one.
        SignOutCommand = new AsyncCommand(
            SignOutAsync, () => _ready() && _remote.UsesCredentialHelper, reportError);

        SaveIdentityCommand = new AsyncCommand(SaveIdentityAsync, CanSaveIdentity, reportError);
    }

    /// <summary>
    /// Something here moved that the shell's own state depends on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces two direct calls this code used to make into the shell — <c>RaiseHealth</c> and
    /// <c>RaiseGitCommandStates</c> — which is the coupling that had to be cut for the class to
    /// move at all. The shell subscribes once and does both.
    /// </para>
    /// <para>
    /// <b>One event rather than two, and that over-notifies slightly on purpose.</b> Only the
    /// identity feeds a git command's <c>CanExecute</c> (<c>CanCommit</c> gates on
    /// <see cref="Identity"/>, so the 17–31 s export never runs for a commit git would refuse);
    /// a credential change strictly needs the health light alone. Sweeping both costs a dozen
    /// <c>RaiseCanExecuteChanged</c> calls and removes the class of bug where a new dependency
    /// is added on one side and the other sweep is not updated — which is exactly what had just
    /// been found in <c>AuthState</c>'s own setter, four commands deep.
    /// </para>
    /// </remarks>
    public event Action? Changed;

    // ----------------------------------------------------------- credentials (plan §12c)

    /// <summary>
    /// The remote's shape: transport, host, owner, repository.
    /// </summary>
    /// <remarks>
    /// <b>Owned here, and read by the shell rather than the other way round.</b> Forge detection
    /// and the merge-request button need it too, so it could have lived on either side. It lives
    /// with the credential because it is the credential's subject — what a failure <i>means</i>
    /// is decided by the transport, and who a credential belongs to is decided by the host.
    /// Splitting the value from the thing that re-reads it on every open is how the two drift.
    /// </remarks>
    public RemoteUrl Remote => _remote;

    /// <summary>
    /// What stands between this machine and the remote, as far as identity is concerned.
    /// </summary>
    /// <remarks>
    /// Set from a proactive check when a repository opens, and from the classification of any
    /// network operation that fails. Cleared the moment one succeeds — a stale "sign in needed"
    /// banner above a working remote is worse than none.
    /// </remarks>
    public GitAuthState AuthState
    {
        get => _authState;
        private set
        {
            if (SetProperty(ref _authState, value))
            {
                OnPropertyChanged(nameof(AuthExplanation));
                OnPropertyChanged(nameof(NeedsAttentionForAuth));
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.RaiseCanExecuteChanged();
                SignOutCommand.RaiseCanExecuteChanged();
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The host the credential belongs to, for the sentence and the lookup.</summary>
    public string RemoteHost => _remote.Host;

    public string AuthExplanation => GitAuthClassifier.Explain(_authState, _remote.Host);

    /// <summary>True whenever there is something to say about credentials.</summary>
    public bool NeedsAttentionForAuth => _authState is not GitAuthState.Fine;

    /// <summary>
    /// True only when signing in could plausibly help.
    /// </summary>
    /// <remarks>
    /// <b>False for a 403 and for an SSH key refusal.</b> Both read like authentication
    /// failures and neither is one: a 403 means the credential worked and the account lacks
    /// access, and SSH has no credential helper at all. Offering the button on either is
    /// offering something that cannot work — and in the 403 case it is a loop that cannot
    /// terminate.
    /// </remarks>
    public bool CanSignIn =>
        _remote.UsesCredentialHelper && GitAuthClassifier.SignInWouldHelp(_authState);

    public AsyncCommand SignInCommand { get; }

    public AsyncCommand SignOutCommand { get; }

    // ------------------------------------------------------------- identity (plan §12b)

    /// <summary>
    /// Who git will attribute a commit to in the open repository.
    /// </summary>
    /// <remarks>
    /// Read at open, and again after the user sets it. The shell's <c>CanCommit</c> gates on it,
    /// so the export gate never runs a 17–31 second CODESYS export for a commit that git was
    /// always going to refuse.
    /// </remarks>
    public GitIdentity Identity
    {
        get => _identity;
        private set
        {
            if (SetProperty(ref _identity, value))
            {
                OnPropertyChanged(nameof(IdentityIsMissing));
                OnPropertyChanged(nameof(IdentityExplanation));
                IdentityName = value.Name;
                IdentityEmail = value.Email;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>True when a repository is open and git could not name a committer.</summary>
    /// <remarks>
    /// Gated on having a repository, because with none open there is nothing to be missing an
    /// identity <i>for</i>, and a prompt on an empty shell is noise.
    /// </remarks>
    public bool IdentityIsMissing => _git is not null && !_identity.IsComplete;

    public string IdentityExplanation => _identity.Explanation;

    /// <summary>Edit buffer for the name field. Separate from <see cref="Identity"/> so typing
    /// does not claim to have configured anything until Save is pressed.</summary>
    public string IdentityName
    {
        get => _identityName;
        set
        {
            if (SetProperty(ref _identityName, value))
            {
                SaveIdentityCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string IdentityEmail
    {
        get => _identityEmail;
        set
        {
            if (SetProperty(ref _identityEmail, value))
            {
                OnPropertyChanged(nameof(IdentityEmailLooksWrong));
                SaveIdentityCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Write to the global config as well as this repository.
    /// </summary>
    /// <remarks>
    /// Off by default. A work machine carrying a personal global identity is a common and
    /// annoying way to attribute commits to the wrong person, and the repository in front of
    /// the user is the one they are answering about.
    /// </remarks>
    public bool IdentityGlobal
    {
        get => _identityGlobal;
        set => SetProperty(ref _identityGlobal, value);
    }

    /// <summary>Advisory: the address does not have the shape of one. Never blocks Save.</summary>
    public bool IdentityEmailLooksWrong =>
        _identityEmail.Length > 0 && !GitIdentity.From(_identityName, _identityEmail).LooksLikeEmail;

    public AsyncCommand SaveIdentityCommand { get; }

    // --------------------------------------------------------------- the shell's hooks

    /// <summary>The repository that just opened, and the client to ask about it.</summary>
    /// <remarks>
    /// The client and the remote arrive at <b>different moments</b> in the open sequence, which
    /// is why this is not one call: the identity has to be read as early as possible — before
    /// the first refresh, so the panel can say "git needs a name" while the user is still
    /// looking at an empty commit box — and the remote URL is not known until <c>origin</c> has
    /// been asked for it.
    /// </remarks>
    public void AttachClient(IGitClient? git) => _git = git;

    /// <summary>
    /// The remote's shape, parsed once when the repository opens.
    /// </summary>
    /// <remarks>
    /// Parsed here rather than re-derived at each failure: what a credential failure means is
    /// decided by the transport, and re-parsing at the point of failure would put string work
    /// on the path where something has already gone wrong.
    /// </remarks>
    public void SetRemote(RemoteUrl remote)
    {
        _remote = remote;
        OnPropertyChanged(nameof(RemoteHost));
        OnPropertyChanged(nameof(AuthExplanation));
        OnPropertyChanged(nameof(CanSignIn));
        SignOutCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Forget the repository being left.</summary>
    /// <remarks>
    /// The identity is deliberately <b>not</b> cleared here, and that is not an oversight:
    /// <see cref="LoadIdentityAsync"/> runs on the way into the next repository and overwrites
    /// it, whereas blanking it now would put "git needs a name" on screen for the moment
    /// between the two, on a machine that has an identity configured perfectly well.
    /// </remarks>
    public void Reset()
    {
        _remote = RemoteUrl.None;
        AuthState = GitAuthState.Fine;
        OnPropertyChanged(nameof(RemoteHost));
    }

    /// <summary>Read who git will attribute a commit to, and let the panel say so.</summary>
    public async Task LoadIdentityAsync(CancellationToken ct)
    {
        if (_git is null)
        {
            Identity = GitIdentity.None;
            return;
        }

        Identity = await _git.IdentityAsync(ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(IdentityIsMissing));

        if (!_identity.IsComplete)
        {
            // Info, not Warn: an unconfigured machine is an ordinary state on a first run, and
            // the panel is already saying so where the user can act on it.
            _logger.Info(LogCategory.Git, $"commit identity incomplete — {_identity.Explanation}");
        }
    }

    /// <summary>Write the name and email the user typed, then re-read what git now reports.</summary>
    /// <remarks>
    /// Re-read rather than assumed. Writing <c>--local</c> into a repository whose global config
    /// already holds a different identity is a case where what git will use and what Versio just
    /// wrote are not obviously the same thing, and the panel must report git's answer.
    /// </remarks>
    private async Task SaveIdentityAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.SaveIdentity");
        using var busy = _busy.Begin(WorkKind.GitWrite, "Setting the git identity…");

        var result = await _git.SetIdentityAsync(
            IdentityName.Trim(), IdentityEmail.Trim(), IdentityGlobal, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            _notifications.Failure("Could not save the identity.",
                                   result.FailureReason ?? string.Empty);
            return;
        }

        await LoadIdentityAsync(busy.Token).ConfigureAwait(true);
        if (_identity.IsComplete)
        {
            _notifications.Success($"Committing as {_identity.Name} <{_identity.Email}>.");
        }
        else
        {
            _notifications.Warning("The identity is still incomplete.", _identity.Explanation);
        }
    }

    private bool CanSaveIdentity() =>
        _git is not null
        && !_busy.IsBusy
        && !string.IsNullOrWhiteSpace(IdentityName)
        && !string.IsNullOrWhiteSpace(IdentityEmail);

    /// <summary>
    /// Ask, before anything fails, whether this machine can talk to the remote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>git credential fill</c> with prompts suppressed. It makes no network call and
    /// cannot prompt, so it costs a process and tells the panel to say "sign in needed" while
    /// the user is still looking at the window — rather than after their first push fails.
    /// </para>
    /// <para>
    /// Only for a remote with a credential helper. An SSH remote's readiness is a question
    /// about keys and an agent, which this cannot answer and must not pretend to.
    /// </para>
    /// </remarks>
    public async Task CheckCredentialAsync(CancellationToken ct)
    {
        if (_git is null || !_remote.UsesCredentialHelper)
        {
            AuthState = GitAuthState.Fine;
            return;
        }

        var stored = await _git.HasStoredCredentialAsync(_remote.Protocol, _remote.Host, ct)
            .ConfigureAwait(true);

        AuthState = stored ? GitAuthState.Fine : GitAuthState.NoCredential;

        if (!stored)
        {
            _logger.Info(LogCategory.Git,
                $"no credential stored for {_remote.Host} — sign-in will be needed");
        }
    }

    /// <summary>
    /// Read a failed network operation and record what it was actually complaining about.
    /// </summary>
    /// <remarks>
    /// Called from every remote operation's failure path, so the banner and the Sign in button
    /// follow the most recent real answer rather than the state at open.
    /// </remarks>
    public void NoteAuthFailure(GitResult result)
    {
        var state = GitAuthClassifier.Classify(result.StdErr, _remote.Transport);

        if (state is not GitAuthState.Fine)
        {
            AuthState = state;
        }
    }

    /// <summary>Clear the banner: something reached the remote and worked.</summary>
    public void NoteAuthSuccess()
    {
        if (_authState is not GitAuthState.Fine)
        {
            _logger.Info(LogCategory.Git, $"the remote accepted us — clearing {_authState}");
            AuthState = GitAuthState.Fine;
        }
    }

    /// <summary>
    /// Classify a failed <b>background</b> fetch, and say whether the poller must stop.
    /// </summary>
    /// <returns>
    /// The sentence to halt the poller with when the refusal is terminal; null when it is not
    /// and polling should carry on.
    /// </returns>
    /// <remarks>
    /// Separate from <see cref="NoteAuthFailure"/> because of the thread and the consequence,
    /// not because of the classification. This one arrives on a timer callback, so the state is
    /// moved <b>on the UI thread</b> — the setter raises property changes into bound controls.
    /// And a terminal refusal has to stop the loop: retrying a rejected credential every few
    /// seconds cannot succeed and may lock an account.
    /// </remarks>
    public string? NoteBackgroundFailure(GitResult result)
    {
        var state = GitAuthClassifier.Classify(result.StdErr, _remote.Transport);

        if (!GitAuthClassifier.IsTerminal(state))
        {
            return null;
        }

        OnUi(() => AuthState = state);
        return GitAuthClassifier.Explain(state, _remote.Host);
    }

    /// <summary>
    /// Hand off to the credential helper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Versio never sees the credential.</b> git invokes whatever helper is configured and
    /// that helper does its own thing — a window, a browser for OAuth, a token prompt. What
    /// comes back here is whether it worked.
    /// </para>
    /// <para>
    /// A rejected credential is <b>erased first</b>. The maddening case is not a missing
    /// credential but a stale one: an expired token the helper keeps cheerfully returning, so
    /// every attempt fails identically. Without the erase, asking again just replays it.
    /// </para>
    /// </remarks>
    private async Task SignInAsync()
    {
        if (_git is null || !_remote.UsesCredentialHelper)
        {
            return;
        }

        _logger.Click("Git.SignIn");

        // Two git calls when a stale credential has to be erased first, and the second only
        // makes sense read beside the first.
        using var scope = _logger.BeginOperation(LoggerExtensions.NewOperationId());

        if (GitAuthClassifier.ShouldEraseFirst(_authState))
        {
            await _git.SignOutAsync(_remote.Protocol, _remote.Host, CancellationToken.None)
                .ConfigureAwait(true);
        }

        // The caption is loud on purpose. The helper's window is a separate process and can
        // come up BEHIND Versio, at which point the application looks frozen while something
        // off-screen waits for a password.
        using var busy = _busy.Begin(
            WorkKind.GitRead,
            $"Signing in to {_remote.Host} — finish in the window that opened (check behind Versio)…");

        var result = await _git.SignInAsync(_remote.Protocol, _remote.Host, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            _notifications.Failure($"Not signed in to {_remote.Host}.",
                                   result.FailureReason ?? string.Empty);
            return;
        }

        AuthState = GitAuthState.Fine;
        _notifications.Success($"Signed in to {_remote.Host}.");
    }

    /// <summary>
    /// Erase the stored credential for this host.
    /// </summary>
    /// <remarks>
    /// The button no git GUI offers, and the one that fixes the failure people actually hit.
    /// Destructive only in the sense that the next network operation will ask again, so it is
    /// not confirmed — nothing is lost that a sign-in cannot restore.
    /// </remarks>
    private async Task SignOutAsync()
    {
        if (_git is null || !_remote.UsesCredentialHelper)
        {
            return;
        }

        _logger.Click("Git.SignOut");
        using var busy = _busy.Begin(
            WorkKind.GitRead, $"Forgetting the credential for {_remote.Host}…");

        var result = await _git.SignOutAsync(_remote.Protocol, _remote.Host, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            _notifications.Failure("Could not erase the stored credential.",
                                   result.FailureReason ?? string.Empty);
            return;
        }

        AuthState = GitAuthState.NoCredential;
        _notifications.Success($"Forgot the credential for {_remote.Host}.",
                               "The next fetch or push will ask for a new one.");
    }

    /// <summary>Re-ask every command in here whether it can run.</summary>
    /// <remarks>
    /// Called by the shell's own sweep. These three are gated on <c>ready</c>, which is the
    /// shell's busy flag, so they move for reasons this class cannot see.
    /// </remarks>
    public void RaiseCommandStates()
    {
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        SaveIdentityCommand.RaiseCanExecuteChanged();
    }
}
