namespace Versio.Core.Git;

/// <summary>What the merge-request button will do, if anything.</summary>
public enum MergeRequestIntent
{
    /// <summary>No button at all — the forge could not be identified.</summary>
    None,

    /// <summary>Open the forge's pre-filled "new request" page.</summary>
    Create,

    /// <summary>Open the request whose head is this branch's tip.</summary>
    GoTo,

    /// <summary>
    /// Open the repository's request list, because the number is not knowable.
    /// </summary>
    /// <remarks>
    /// Azure DevOps and Bitbucket publish no request refs, so on those forges a branch that
    /// may well have an open request cannot be linked to it directly. The list is the honest
    /// answer; a fabricated number is not.
    /// </remarks>
    GoToList,
}

/// <summary>
/// The merge-request button: whether it exists, what it says, and where it goes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent and disabled are different states, and the difference is the point.</b> A forge
/// that cannot be identified produces no button — there is nothing to explain and nothing the
/// user can do about it from here. Everything else produces a live or a dead button that says
/// why, because those are all states the user can leave by doing something.
/// </para>
/// <para>
/// <b>Why the gates are what they are.</b> A request from work that is still local would be
/// named after changes it does not contain; a request from a dirty tree would be missing the
/// edit somebody is looking at; and a request from the default branch into itself is not a
/// thing. Each of those is a page that opens successfully and is wrong, which is worse than a
/// button that refuses and says so.
/// </para>
/// <para>
/// Pure, like <see cref="CommitPlan"/> beside it: the shell owns the URL opening and the ref
/// probe, and every rule below is asserted without a repository.
/// </para>
/// </remarks>
public sealed record MergeRequestPlan
{
    /// <summary>Nothing to show. The button is not rendered.</summary>
    public static readonly MergeRequestPlan Hidden = new()
    {
        Intent = MergeRequestIntent.None,
        Label = string.Empty,
        Url = string.Empty,
        BlockedReason = string.Empty,
    };

    public required MergeRequestIntent Intent { get; init; }

    /// <summary>The button's caption, in the forge's own vocabulary.</summary>
    public required string Label { get; init; }

    /// <summary>Where it goes. Empty while the button is blocked.</summary>
    public required string Url { get; init; }

    /// <summary>Why it is dead, or empty when it is live.</summary>
    public required string BlockedReason { get; init; }

    /// <summary>The button exists at all.</summary>
    public bool IsVisible => Intent is not MergeRequestIntent.None;

    /// <summary>The button is live.</summary>
    public bool CanOpen => IsVisible && BlockedReason.Length == 0 && Url.Length > 0;

    /// <summary>
    /// Work out the button from everything that bears on it.
    /// </summary>
    /// <param name="kind">The forge, already detected.</param>
    /// <param name="remote">The remote, parsed, for building the URL.</param>
    /// <param name="branch">The current branch and how far it has diverged.</param>
    /// <param name="tracking">What the remote row is already showing.</param>
    /// <param name="defaultBranch">The trunk. A request into itself is not a thing.</param>
    /// <param name="isDirty">Anything staged, unstaged or conflicted.</param>
    /// <param name="existingNumber">
    /// The request number a ref probe found, or null. Null on a forge that publishes no refs is
    /// indistinguishable from "no request exists", which is why those degrade to the list.
    /// </param>
    public static MergeRequestPlan For(
        ForgeKind kind,
        RemoteUrl remote,
        BranchInfo? branch,
        TrackingState tracking,
        DefaultBranch defaultBranch,
        bool isDirty,
        int? existingNumber = null)
    {
        var noun = Forge.Noun(kind);

        // No forge, no button. Not a disabled one: there is nothing here for the user to fix,
        // and a permanently dead control with a tooltip about repo.json is clutter on every
        // repository that will never have a forge — which includes every local-only one.
        if (kind is ForgeKind.Unknown || branch is null || branch.IsDetached)
        {
            return Hidden;
        }

        var create = Forge.CreateUrl(kind, remote, branch.Name, defaultBranch.Name);
        if (create.Length == 0 && !defaultBranch.IsKnown)
        {
            // The trunk is what a request targets, and DefaultBranch already refuses to guess
            // it. Its own wording names the setting, so it is passed through rather than
            // reworded.
            return Blocked(kind, defaultBranch.BlockedReason);
        }

        if (create.Length == 0)
        {
            return Hidden;
        }

        // Ordered by what to do first, and the order is load-bearing: somebody on the default
        // branch has nothing to fix, somebody with uncommitted work commits before they push,
        // and a branch with no upstream is published rather than pushed.
        if (defaultBranch.IsKnown &&
            branch.Name.Equals(defaultBranch.Name, StringComparison.Ordinal))
        {
            return Blocked(kind, $"This is the default branch — a {noun} into itself is not a thing.");
        }

        if (isDirty)
        {
            return Blocked(kind, "Commit your changes first.");
        }

        if (tracking is TrackingState.NoUpstream)
        {
            return Blocked(kind, "Publish this branch first.");
        }

        if (tracking is TrackingState.NoRemote or TrackingState.Unborn
            or TrackingState.NoRepository or TrackingState.Detached)
        {
            return Hidden;
        }

        if (branch.Ahead > 0)
        {
            return Blocked(kind,
                $"Push your {branch.Ahead} commit{(branch.Ahead == 1 ? string.Empty : "s")} first — " +
                $"a {noun} would be missing them.");
        }

        if (existingNumber is { } number)
        {
            return new MergeRequestPlan
            {
                Intent = MergeRequestIntent.GoTo,

                // "Go to", never "1 open pull request". A ref says a request exists whose head
                // is this commit; it does not say whether it is open, merged or a draft.
                Label = $"Go to {noun}",
                Url = Forge.ExistingUrl(kind, remote, number),
                BlockedReason = string.Empty,
            };
        }

        // A forge with no request refs cannot answer "does one exist?", so Create would be
        // wrong as often as it is right. The list page is true either way.
        if (Forge.RequestRefPattern(kind).Length == 0)
        {
            return new MergeRequestPlan
            {
                Intent = MergeRequestIntent.GoToList,
                Label = $"Open {noun}s",
                Url = Forge.ListUrl(kind, remote),
                BlockedReason = string.Empty,
            };
        }

        return new MergeRequestPlan
        {
            Intent = MergeRequestIntent.Create,
            Label = $"Create {noun}",
            Url = create,
            BlockedReason = string.Empty,
        };
    }

    /// <summary>
    /// A visible, dead button that says what to do about it.
    /// </summary>
    /// <remarks>
    /// <b>The caption matches what this forge's live button would say</b>, rather than "Create"
    /// everywhere. On Azure DevOps and Bitbucket the live button never says Create — they
    /// publish no request refs, so it offers the list — and a control that read "Create pull
    /// request" while dead and "Open pull requests" once live would be changing the verb under
    /// somebody as they fixed the thing it asked them to fix.
    /// </remarks>
    private static MergeRequestPlan Blocked(ForgeKind kind, string reason)
    {
        var lists = Forge.RequestRefPattern(kind).Length == 0;

        return new MergeRequestPlan
        {
            Intent = lists ? MergeRequestIntent.GoToList : MergeRequestIntent.Create,
            Label = lists ? $"Open {Forge.Noun(kind)}s" : $"Create {Forge.Noun(kind)}",
            Url = string.Empty,
            BlockedReason = reason,
        };
    }
}
