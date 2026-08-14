namespace Versio.Core.Git;

/// <summary>Everything the status light looks at, gathered in one place.</summary>
/// <remarks>
/// A record rather than seven positional booleans, and that is not decoration: the conditions
/// are all <see cref="bool"/> and a caller that swapped two of them would compile, run, and
/// paint the wrong colour for the wrong reason. Named properties make the call site say what
/// it is passing, and adding an eighth condition cannot silently shift the other seven.
/// </remarks>
public sealed record ShellConditions
{
    /// <summary>False before anything has been opened, which is a state and not a fault.</summary>
    public bool RepositoryOpen { get; init; }

    /// <summary>The launch probe has come back and said git is not on this machine.</summary>
    public bool GitMissing { get; init; }

    /// <summary>The project whose sync failed, or empty. Named, because the sentence names it.</summary>
    public string FailedProject { get; init; } = string.Empty;

    public bool MidMerge { get; init; }

    public bool IdentityMissing { get; init; }

    public bool AuthNeedsAttention { get; init; }

    public bool Diverged { get; init; }
}

/// <summary>
/// What the status light in the corner of the window is actually saying.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this replaces.</b> The status bar drew
/// <c>&lt;Ellipse Fill="{DynamicResource Brush.Status.Added}" /&gt;</c> — a hardcoded green
/// dot, in the leftmost position of the status bar, shaped exactly like a health indicator.
/// It was green while git was missing, while the tree was mid-merge, while a sync had failed
/// and while the branch had diverged. The shell computed every one of those states and the
/// status bar read none of them, so the one thing on screen that looked like a summary of
/// whether anything was wrong could only ever be right by accident.
/// </para>
/// <para>
/// Pure, and in Core, for the reason <c>RecentRepositories</c> and <c>RemoteStanding</c> are:
/// the part worth testing is the precedence between seven conditions, and none of it needs a
/// repository, a process or a clock.
/// </para>
/// <para>
/// A brush KEY rather than a brush, following the convention every row in the application
/// already uses — the view model decides what something means, the theme decides what it
/// looks like.
/// </para>
/// </remarks>
public sealed record ShellHealth
{
    public required string BrushKey { get; init; }

    /// <summary>Why it is that colour, for the tooltip. A colour alone is not an explanation.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// The worst thing that is true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ordered, and the order is the design.</b> Several of these are true at once
    /// routinely — a mid-merge branch is usually also diverged, and a machine with no git also
    /// has no identity — so the light reports the condition that has to be dealt with FIRST
    /// rather than the last one checked.
    /// </para>
    /// <para>
    /// Git missing leads because nothing else in the application can work without it, which is
    /// the same argument the git panel's own banner order makes. A failed sync outranks a
    /// merge because a failed project stops syncing until a person has seen the failure, and
    /// that gate is deliberate.
    /// </para>
    /// <para>
    /// <b>No repository open is neutral, not green.</b> It is the state the app launches in,
    /// and a green light on an empty shell would be reporting the health of nothing.
    /// </para>
    /// </remarks>
    public static ShellHealth Evaluate(ShellConditions conditions)
    {
        if (conditions.GitMissing)
        {
            return new ShellHealth
            {
                BrushKey = "Brush.Status.Deleted",
                Summary = "Git is not available on this machine",
            };
        }

        if (!conditions.RepositoryOpen)
        {
            return new ShellHealth
            {
                BrushKey = "Brush.Status.Neutral",
                Summary = "No repository open",
            };
        }

        if (conditions.FailedProject.Length > 0)
        {
            return new ShellHealth
            {
                BrushKey = "Brush.Status.Deleted",
                Summary = $"{conditions.FailedProject} failed to sync and is waiting for you",
            };
        }

        if (conditions.MidMerge)
        {
            return new ShellHealth
            {
                BrushKey = "Brush.Status.Conflict",
                Summary = "A merge is part-way through",
            };
        }

        if (conditions.IdentityMissing)
        {
            return new ShellHealth
            {
                BrushKey = "Brush.Status.Modified",
                Summary = "Git cannot name a committer yet",
            };
        }

        if (conditions.AuthNeedsAttention)
        {
            return new ShellHealth
            {
                BrushKey = "Brush.Status.Modified",
                Summary = "The remote needs attention before it will talk to you",
            };
        }

        if (conditions.Diverged)
        {
            return new ShellHealth
            {
                BrushKey = "Brush.Status.Modified",
                Summary = "This branch and the remote have both moved",
            };
        }

        return new ShellHealth
        {
            BrushKey = "Brush.Status.Added",
            Summary = "Nothing needs your attention",
        };
    }
}
