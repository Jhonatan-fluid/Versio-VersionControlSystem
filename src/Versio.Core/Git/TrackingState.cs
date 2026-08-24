namespace Versio.Core.Git;

/// <summary>Where this branch stands in relation to the remote.</summary>
public enum TrackingState
{
    /// <summary>No repository is open.</summary>
    NoRepository,

    /// <summary>A repository with no commits yet, where HEAD points nowhere.</summary>
    Unborn,

    /// <summary>Not on a branch at all — HEAD is a commit.</summary>
    Detached,

    /// <summary>The repository has no remote configured, so there is nowhere to push.</summary>
    NoRemote,

    /// <summary>There is a remote, but this branch has never been pushed to it.</summary>
    NoUpstream,

    /// <summary>Level with the upstream.</summary>
    InSync,

    /// <summary>Local commits the remote does not have.</summary>
    Ahead,

    /// <summary>Remote commits this branch does not have.</summary>
    Behind,

    /// <summary>Both, which no fast-forward can resolve.</summary>
    Diverged,

    /// <summary>
    /// An upstream is configured but its branch is gone from the remote — deleted after a
    /// merge, usually.
    /// </summary>
    /// <remarks>
    /// The state that used to render as <see cref="InSync"/>: with the remote branch deleted,
    /// ahead/behind decays to 0/0 and the badge said "Up to date" with a branch nobody else
    /// could see any more (FINDINGS 2026-08-23).
    /// </remarks>
    UpstreamGone,
}

/// <summary>
/// The one question somebody opens this panel to answer: is my work safe on the remote?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a type rather than two integers.</b> The panel used to answer it with
/// <c>↑2 ↓1</c> in a caption-sized mono glyph, which has three problems. Ahead and behind look
/// alike at a glance and mean opposite things — one is routine, the other means somebody else
/// moved and you have not caught up. An empty string meant <i>both</i> "in sync" and "no
/// upstream", which are not remotely the same situation. And a branch that has never been
/// pushed reported <c>0/0</c>, i.e. it rendered as perfectly in sync while being invisible to
/// everybody else on the team.
/// </para>
/// <para>
/// <b>Every state names its remedy in words.</b> A badge that says <i>"the remote has changes
/// you do not have"</i> is actionable where <c>↓2</c> is a puzzle. This is the same principle
/// <c>GuardNarrative</c> follows for refusals, and for the same reason: the sentence is part of
/// the feature.
/// </para>
/// <para>
/// <b>Pure, and it takes what it needs rather than a git client.</b> Everything here is derived
/// from a <see cref="BranchInfo"/> and one boolean about whether any remote exists, so all
/// eleven states are asserted without a repository, a process or a clock.
/// </para>
/// </remarks>
public sealed record RemoteStanding
{
    public required TrackingState State { get; init; }

    public int Ahead { get; init; }

    public int Behind { get; init; }

    /// <summary>The upstream's short name, when there is one.</summary>
    public string Upstream { get; init; } = string.Empty;

    /// <summary>The full sentence for the badge.</summary>
    public required string Headline { get; init; }

    /// <summary>
    /// Which semantic colour this deserves.
    /// </summary>
    /// <remarks>
    /// A key rather than a brush, so the view model decides the meaning and the theme decides
    /// the paint — the convention every row in this application already follows.
    /// <para>
    /// <b>Amber is reserved for "somebody else moved, or nobody can see your work".</b> Being
    /// ahead is normal and is stated without being coloured like a problem; being behind,
    /// diverged, or unpublished are all states where doing nothing has a cost.
    /// </para>
    /// </remarks>
    public required string BrushKey { get; init; }

    /// <summary>True when there is an upstream to pull from or push to.</summary>
    public bool HasUpstream => Upstream.Length > 0;

    /// <summary>True when pushing could possibly do anything.</summary>
    /// <remarks>
    /// <b><see cref="TrackingState.Diverged"/> counts.</b> Git will reject that push, and the
    /// button stays live anyway: pre-emptively disabling it hides the state the user has to
    /// resolve, where git's own rejection — now visible in a notification that does not expire
    /// — is the clearest possible explanation of what is wrong.
    /// <para>
    /// <b><see cref="TrackingState.UpstreamGone"/> counts too.</b> Pushing to a deleted
    /// upstream recreates the branch on the remote, which is exactly the "push to republish"
    /// that state's headline offers.
    /// </para>
    /// </remarks>
    public bool CanPush => State is TrackingState.Ahead or TrackingState.Diverged
                                 or TrackingState.UpstreamGone;

    // There is no `Primary` here, and there was.
    //
    // A RemoteAction enum named which button each state was asking for - and nothing ever
    // read it. The panel swaps on CanPush / CanPublish / CanPull, which is what a XAML
    // Visibility binding can use directly, and the extra layer only meant two places could
    // disagree about which button belonged to a state. Removed rather than wired, for the
    // reason GuardIntent's own doc comment now carries: every member has a caller, or says
    // why it does not.

    /// <summary>True when this branch is not on the remote and could be put there.</summary>
    public bool CanPublish => State == TrackingState.NoUpstream;

    /// <summary>True when a fast-forward cannot resolve this and the user must choose.</summary>
    /// <remarks>
    /// The panel offers a rebase or a merge only here. Everywhere else the choice would be
    /// noise, and `--ff-only` is the right default precisely because auto-merging native
    /// CODESYS XML produces a file nobody reviewed.
    /// </remarks>
    public bool IsDiverged => State == TrackingState.Diverged;

    /// <summary>True when HEAD is on no branch at all, and the way out has to be offered.</summary>
    /// <remarks>
    /// <b>The one state whose sentence is not enough on its own.</b> Every other standing either
    /// needs no action or has its action on the card already — push, publish, pull, reconcile.
    /// This one names a trap (work committed here is reachable only from the reflog) and the
    /// only escape is creating a branch, which lived in the top bar's <c>+</c> — a menu, which is
    /// what somebody who arrived here by clicking a commit in the history has no reason to open.
    /// <para>
    /// So the panel puts the escape inside the sentence that describes the trap. Exposed as a
    /// predicate rather than compared against <see cref="TrackingState.Detached"/> in the XAML
    /// for the same reason <see cref="IsDiverged"/> is: a binding cannot compare an enum without
    /// a converter, and the rule belongs beside the state machine that decides it.
    /// </para>
    /// </remarks>
    public bool IsDetached => State == TrackingState.Detached;

    /// <summary>
    /// True whenever there is an upstream that can actually be pulled from.
    /// </summary>
    /// <remarks>
    /// <b>Not gated on <see cref="Behind"/>.</b> Behind-ness is only ever as fresh as the last
    /// fetch, and disabling a safe fast-forward pull on possibly-stale knowledge would leave
    /// somebody unable to act on a remote that had moved a second ago. A pull that reports
    /// "already up to date" costs nothing; a dead button costs a trip to the terminal.
    /// <para>
    /// <b>Except when the upstream is gone.</b> That argument rests on a useless pull being
    /// harmless, and a pull from a deleted branch is not harmless-useless but a guaranteed
    /// error ("no such ref was fetched") — a button that can only fail teaches people to
    /// distrust the panel.
    /// </para>
    /// </remarks>
    public bool CanPull => HasUpstream && State != TrackingState.UpstreamGone;

    /// <summary>Suffix for a button label, so the count informs without gating. Empty when
    /// there is nothing to count.</summary>
    public string PushCountSuffix => Ahead > 0 ? $" ({Ahead})" : string.Empty;

    public string PullCountSuffix => Behind > 0 ? $" ({Behind})" : string.Empty;

    /// <summary>Nothing is open.</summary>
    public static readonly RemoteStanding None = new()
    {
        State = TrackingState.NoRepository,
        Headline = string.Empty,
        BrushKey = TextSecondary,
    };

    private const string TextSecondary = "Brush.Text.Secondary";
    private const string Amber = "Brush.Status.Modified";
    private const string Accent = "Brush.Accent.Default";

    /// <summary>
    /// Work out where a branch stands.
    /// </summary>
    /// <param name="branch">The branch as porcelain v2 reported it.</param>
    /// <param name="hasRemote">
    /// Whether the repository has any remote at all. <b>Required to tell
    /// <see cref="TrackingState.NoRemote"/> from <see cref="TrackingState.NoUpstream"/></b>,
    /// which look identical from the branch alone and have completely different remedies: one
    /// is "add a remote", the other is "press Publish". Answering the wrong one sends somebody
    /// looking for a setting that is already correct.
    /// </param>
    public static RemoteStanding For(BranchInfo branch, bool hasRemote)
    {
        if (branch.IsUnborn)
        {
            return new RemoteStanding
            {
                State = TrackingState.Unborn,
                Headline = "No commits yet. Make the first one and this branch begins.",
                BrushKey = TextSecondary,
            };
        }

        if (branch.IsDetached)
        {
            // Reached by accident, usually from clicking a commit in the history, and there is
            // no way out of it from inside the app without saying what it is. A commit made
            // here is real and reachable only from the reflog, which is the trap.
            return new RemoteStanding
            {
                State = TrackingState.Detached,
                Headline = "Not on a branch — you are looking at a commit. " +
                           "New work here will be lost unless you put it on a branch.",
                BrushKey = Amber,
            };
        }

        if (!hasRemote)
        {
            return new RemoteStanding
            {
                State = TrackingState.NoRemote,
                Headline = "This repository has no remote, so there is nowhere to push.",
                BrushKey = TextSecondary,
            };
        }

        if (branch.Upstream is not { Length: > 0 } upstream)
        {
            return new RemoteStanding
            {
                State = TrackingState.NoUpstream,
                Headline = $"Not on the remote yet. Nobody else can see {branch.Name}.",
                BrushKey = Amber,
            };
        }

        if (branch.UpstreamGone)
        {
            // Porcelain states this by omission — an upstream and no ahead/behind line — and
            // the zeros that omission leaves behind used to fall through to InSync below:
            // "Up to date with origin/x" over a branch the forge deleted after merging it.
            // Amber for the same reason NoUpstream is: the work is invisible to everybody
            // else, and doing nothing has a cost.
            return new RemoteStanding
            {
                State = TrackingState.UpstreamGone,
                Upstream = upstream,
                Headline = $"{upstream} is gone from the remote — deleted after a merge, " +
                           "usually. Push to republish it, or switch to another branch.",
                BrushKey = Amber,
            };
        }

        var (ahead, behind) = (branch.Ahead, branch.Behind);

        return (ahead, behind) switch
        {
            (0, 0) => new RemoteStanding
            {
                State = TrackingState.InSync,
                Ahead = 0,
                Behind = 0,
                Upstream = upstream,
                Headline = $"Up to date with {upstream}.",
                BrushKey = TextSecondary,
            },

            ( > 0, 0) => new RemoteStanding
            {
                State = TrackingState.Ahead,
                Ahead = ahead,
                Upstream = upstream,
                Headline = ahead == 1
                    ? "1 commit to push."
                    : $"{ahead} commits to push.",

                // Not amber. Work that has not been pushed yet is the normal condition of
                // somebody mid-task, and colouring it like a problem would make the colour
                // mean nothing by the time it is needed.
                BrushKey = Accent,
            },

            (0, > 0) => new RemoteStanding
            {
                State = TrackingState.Behind,
                Behind = behind,
                Upstream = upstream,
                Headline = behind == 1
                    ? "1 commit to pull — the remote has a change you do not have."
                    : $"{behind} commits to pull — the remote has changes you do not have.",
                BrushKey = Amber,
            },

            _ => new RemoteStanding
            {
                State = TrackingState.Diverged,
                Ahead = ahead,
                Behind = behind,
                Upstream = upstream,

                // Says what to do, because --ff-only will refuse this and the user needs to
                // know why before they press anything.
                Headline = $"Diverged — {ahead} to push, {behind} to pull. Pull first.",
                BrushKey = Amber,
            },
        };
    }
}
