using System.Windows;
using System.Windows.Controls;
using Versio.Core.Git;

namespace Versio.App.Views;

/// <summary>One branch in the merge list.</summary>
/// <remarks>
/// A row of its own rather than <see cref="BranchCandidate"/> directly, on the same argument
/// <see cref="BranchDeleteRow"/> makes: the candidate describes what git reports, and this
/// describes how it reads on screen. What it does NOT carry is a tick — this dialog chooses
/// one branch through the list's own selection.
/// </remarks>
public sealed class BranchMergeRow(BranchCandidate candidate, bool alreadyIn)
{
    public BranchCandidate Candidate { get; } = candidate;

    public string Name => Candidate.Name;

    /// <summary>Already contained in the branch being merged into, so merging is a no-op.</summary>
    public bool AlreadyIn { get; } = alreadyIn;

    /// <summary>The current branch cannot be merged into itself.</summary>
    public bool IsCurrent => Candidate.IsCurrent;

    /// <summary>
    /// On the remote only, so its short name resolves to nothing on this machine.
    /// </summary>
    /// <remarks>
    /// <b>Offered but not selectable, rather than hidden.</b> Hiding it would make a branch
    /// somebody can see in the branch dropdown vanish from the merge list with no explanation.
    /// It cannot be merged by the name shown because there is no local ref for it —
    /// <c>git diff HEAD...feature</c> simply fails — and merging it properly means naming its
    /// remote ref, which is a small piece of work this dialog does not do yet.
    /// </remarks>
    public bool IsRemoteOnly => Candidate.Existence == BranchExistence.RemoteOnly;

    public bool CanMerge => !AlreadyIn && !IsCurrent && !IsRemoteOnly;

    public string WhereText => Candidate.Existence switch
    {
        BranchExistence.LocalOnly => "local only",
        BranchExistence.RemoteOnly => "remote only",
        _ => "local + remote",
    };

    /// <summary>
    /// The column worth reading twice — and it means the opposite of the delete dialog's.
    /// </summary>
    /// <remarks>
    /// There, "merged" means a branch is safe to delete. Here it means merging it would do
    /// nothing at all. Same word, same data, opposite consequence — which is exactly why it is
    /// spelled out rather than shown as a tick.
    /// </remarks>
    public string MergedText =>
        IsCurrent ? "this branch"
        : IsRemoteOnly ? "not here yet"
        : AlreadyIn ? "already in"
        : "has new work";

    public string MergedBrushKey =>
        CanMerge ? "Brush.Status.Added" : "Brush.Text.Disabled";
}

/// <summary>
/// Choose one branch to merge into the current one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One branch, not a set</b>, unlike the delete dialog it is modelled on. Merging two
/// branches in one gesture is two merges behind one confirmation, and the second would land on
/// a tree nobody has looked at — the first may well have conflicted.
/// </para>
/// <para>
/// <b>The dialog collects the choice and nothing else.</b> Whether the merge may actually
/// happen — the guard over uncommitted work, and the refusal when it would reach a tracked
/// project — stays in the view model, where the repository is. The same division the delete
/// dialog records.
/// </para>
/// </remarks>
public partial class BranchMergeWindow : Window
{
    private readonly List<BranchMergeRow> _rows;

    public BranchMergeWindow(IReadOnlyList<BranchCandidate> candidates, string into)
    {
        InitializeComponent();

        _rows = [.. candidates.Select(c => new BranchMergeRow(c, c.IsMerged))];

        Headline.Text = $"Choose a branch to merge into {into}.";
        BranchList.ItemsSource = _rows;
        BranchList.SelectionChanged += (_, _) => Describe();

        MergeButton.Click += (_, _) =>
        {
            Result = Selected?.Name;
            DialogResult = true;
        };

        Describe();
    }

    /// <summary>The branch chosen, or null when cancelled.</summary>
    public string? Result { get; private set; }

    private BranchMergeRow? Selected => BranchList.SelectedItem as BranchMergeRow;

    /// <summary>
    /// Keep the button and its reason in step with the selection.
    /// </summary>
    /// <remarks>
    /// The reason is stated rather than left to a greyed button. Two of the three cases are
    /// guessable — the current branch, a branch already in — and the third is not: "already in"
    /// on a branch somebody just pushed reads as a bug until the sentence explains that the
    /// work is here already.
    /// </remarks>
    private void Describe()
    {
        var row = Selected;

        MergeButton.IsEnabled = row?.CanMerge == true;

        Reason.Text = row switch
        {
            null => "Nothing selected yet.",
            { IsCurrent: true } => $"{row.Name} is the branch you are on.",
            { IsRemoteOnly: true } =>
                $"{row.Name} is only on the remote. Check it out once so this machine has " +
                "it, and it can be merged from then on.",
            { AlreadyIn: true } =>
                $"{row.Name} is already in this branch — merging it would change nothing.",
            _ => string.Empty,
        };

        MergeButton.ToolTip = row?.CanMerge == true
            ? $"Bring {row.Name}'s commits into this branch"
            : Reason.Text;
    }
}
