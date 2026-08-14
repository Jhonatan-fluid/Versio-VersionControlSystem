using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Versio.Core.Git;

namespace Versio.App.Views;

/// <summary>One branch in the delete list, with whatever the user has said about it.</summary>
/// <remarks>
/// A row rather than the <see cref="BranchCandidate"/> itself, because a tick is view state:
/// the candidate describes what git reports, and this describes what the user has chosen. The
/// dialog hands the plan back and the shell decides whether it may happen.
/// </remarks>
public sealed class BranchDeleteRow(BranchCandidate candidate) : INotifyPropertyChanged
{
    private bool _isChosen;

    public BranchCandidate Candidate { get; } = candidate;

    public string Name => Candidate.Name;

    public bool CanSelect => Candidate.CanSelect;

    /// <summary>Why this row cannot be ticked, shown even while the checkbox is disabled.</summary>
    public string Blocked => Candidate.Blocked;

    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            // A row that cannot be selected stays unselected however it is asked. Belt and
            // braces beside the disabled checkbox: the collection is also read by code.
            var wanted = value && CanSelect;

            if (_isChosen == wanted)
            {
                return;
            }

            _isChosen = wanted;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChosen)));
            Chosen?.Invoke();
        }
    }

    /// <summary>Raised so the dialog can recompute its summary without watching every row.</summary>
    public event Action? Chosen;

    public string WhereText => Candidate.Existence switch
    {
        BranchExistence.LocalOnly => "local only",
        BranchExistence.RemoteOnly => "remote only",
        _ => "local + remote",
    };

    /// <summary>The one column worth reading twice.</summary>
    public string MergedText => Candidate.IsMerged ? "merged" : "not merged";

    /// <summary>A key rather than a brush, so the theme decides the paint.</summary>
    public string MergedBrushKey =>
        Candidate.IsMerged ? "Brush.Text.Disabled" : "Brush.Status.Modified";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Choose branches to delete, and how far the deletion reaches.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dialog decides what; the shell decides whether.</b> Everything here is selection and
/// wording — the guard, the confirmation for a remote delete, the checkout when the current
/// branch is chosen, and the per-branch reporting all live where the repository is. A dialog
/// that ran git would be a second place those rules could be got wrong.
/// </para>
/// <para>
/// Constructed against the real stylesheet by a test, for the reason <c>MainWindowTests</c>
/// documents at length: a <c>StaticResource</c> is a key until something asks for it, and the
/// first thing that asks is the window opening in front of somebody.
/// </para>
/// </remarks>
public partial class BranchDeleteWindow : Window
{
    private readonly ObservableCollection<BranchDeleteRow> _rows = [];

    public BranchDeleteWindow(IReadOnlyList<BranchCandidate> candidates)
    {
        InitializeComponent();

        foreach (var candidate in candidates)
        {
            var row = new BranchDeleteRow(candidate);
            row.Chosen += Refresh;
            _rows.Add(row);
        }

        BranchList.ItemsSource = _rows;
        ForceCheck.Checked += (_, _) => Refresh();
        ForceCheck.Unchecked += (_, _) => Refresh();
        RemoteCheck.Checked += (_, _) => Refresh();
        RemoteCheck.Unchecked += (_, _) => Refresh();
        DeleteButton.Click += OnDelete;

        Refresh();
    }

    /// <summary>What the user chose, or null if they cancelled.</summary>
    public DeletePlan? Result { get; private set; }

    /// <summary>The plan as it stands, for the summary and the button.</summary>
    internal DeletePlan Current => new()
    {
        Selected = [.. _rows.Where(r => r.IsChosen).Select(r => r.Candidate)],
        Scope = RemoteCheck.IsChecked == true ? DeleteScope.LocalAndRemote : DeleteScope.Local,
        ForceAcknowledged = ForceCheck.IsChecked == true,
    };

    /// <summary>Keep the summary, the force checkbox and the button in step with the ticks.</summary>
    private void Refresh()
    {
        var plan = Current;

        SummaryText.Text = plan.Summary;

        // The acknowledgement only appears when there is something to acknowledge. A permanent
        // "delete unmerged work" checkbox would be read once and never again.
        ForceCheck.Visibility = plan.HasUnmerged ? Visibility.Visible : Visibility.Collapsed;

        if (!plan.HasUnmerged)
        {
            ForceCheck.IsChecked = false;
        }

        DeleteButton.IsEnabled = plan.CanProceed;
        DeleteButton.ToolTip = plan.CanProceed
            ? "Delete the chosen branches"
            : plan.BlockedReason;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var plan = Current;

        if (!plan.CanProceed)
        {
            return;
        }

        Result = plan;
        DialogResult = true;
    }
}
