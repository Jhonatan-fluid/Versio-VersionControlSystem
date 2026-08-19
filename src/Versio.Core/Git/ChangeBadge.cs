namespace Versio.Core.Git;

/// <summary>
/// The count on the rail: how much is waiting in the working tree, and how loudly to say it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> The only count on screen was a pill in the panel <i>title</i>,
/// and the shell showed it on the Project panel alone. So sitting on History — reading what
/// already happened, which is exactly when somebody is about to act — the application said
/// nothing at all about the working tree: twelve changed files and none looked identical. The
/// one count that existed was on the panel you were already looking at, which is the place it
/// is least needed.
/// </para>
/// <para>
/// <b>In Core because it is a rule with cases, not a number.</b> Suppression at zero, the cap,
/// and conflicts outranking the count are three decisions that can be got wrong independently,
/// and none of them needs a window to check. Same argument as
/// <see cref="ShellHealth"/> one file along, which was written after a hardcoded green dot
/// spent months claiming everything was fine.
/// </para>
/// </remarks>
public readonly record struct ChangeBadge
{
    /// <summary>
    /// The point past which the exact number stops being worth its width.
    /// </summary>
    /// <remarks>
    /// A rail tile is 40 px. An import or a branch switch across a real project produces
    /// hundreds of mirror rows, and <c>438</c> in that space is either unreadable or pushes the
    /// badge wider than the icon it sits on. The distinction that matters at this size is
    /// <i>some</i> against <i>a lot</i>, and the exact figure is one click away in the panel.
    /// </remarks>
    public const int Cap = 99;

    /// <summary>False when there is nothing to say, which is the ordinary state.</summary>
    /// <remarks>
    /// <b>Suppressed at zero rather than showing <c>0</c>.</b> The pill this replaces had no
    /// zero-suppression of any kind, so a clean tree drew a badge containing <c>0</c> beside a
    /// panel whose empty state already said so in words. A caption that reads the same on every
    /// refresh is read once and never again — the argument this codebase has already had over a
    /// coverage line that congratulated itself every time.
    /// </remarks>
    public bool IsVisible { get; init; }

    /// <summary>The number, or <c>99+</c> past <see cref="Cap"/>.</summary>
    public string Text { get; init; }

    /// <summary>
    /// Which theme brush paints it — the view model decides the meaning, the theme the paint.
    /// </summary>
    public string BrushKey { get; init; }

    /// <summary>Nothing open, or nothing changed.</summary>
    public static readonly ChangeBadge None = new()
    {
        IsVisible = false,
        Text = string.Empty,
        BrushKey = Accent,
    };

    private const string Accent = "Brush.Accent.Default";
    private const string Conflict = "Brush.Status.Deleted";

    /// <summary>
    /// What the rail should show for a working tree in this state.
    /// </summary>
    /// <remarks>
    /// <b>A conflicted tree outranks a busy one, and that is the whole reason for the colour.</b>
    /// Both states produce a count, and they are not the same news: a dozen edits is ordinary
    /// work, while a single unmerged path means git refuses the commit and the sync engine
    /// refuses both directions. A badge that showed the second as though it were the first
    /// would be a summary that can only be right by accident — which is exactly what the status
    /// light was doing before <see cref="ShellHealth"/> existed.
    /// </remarks>
    /// <param name="changed">Rows git reports in the working tree, staged and not.</param>
    /// <param name="conflicted">True while any of them is unmerged.</param>
    public static ChangeBadge For(int changed, bool conflicted)
    {
        if (changed <= 0)
        {
            return None;
        }

        return new ChangeBadge
        {
            IsVisible = true,
            Text = changed > Cap ? $"{Cap}+" : changed.ToString(),
            BrushKey = conflicted ? Conflict : Accent,
        };
    }
}
