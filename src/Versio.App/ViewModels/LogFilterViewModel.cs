using System.Collections.ObjectModel;
using Versio.App.Mvvm;
using Versio.Core.Logging;
using Versio.Plc.Codesys;
using Versio.Plc.TiaPortal;

namespace Versio.App.ViewModels;

/// <summary>
/// One level's toggle in the log header: click to include, click again to drop.
/// </summary>
/// <remarks>
/// A set, not a threshold. The panel used to filter by minimum level, which cannot express
/// the request somebody actually has when a log is misbehaving — <i>show me the warnings and
/// errors and nothing else</i>. A threshold answers "how much detail" when the real question
/// is "which of these four things am I looking for", and at Debug, which is the default, it
/// filters nothing at all.
/// </remarks>
public sealed class LevelToggle(LogLevel level, string label, string brushKey, bool enabled)
    : ObservableObject
{
    private bool _isEnabled = enabled;

    public LogLevel Level { get; } = level;

    /// <summary>The three-letter form already used in the rows, so the two read as one thing.</summary>
    public string Label { get; } = label;

    /// <summary>Theme key, so a toggle carries the same colour as the rows it admits.</summary>
    public string BrushKey { get; } = brushKey;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}

/// <summary>
/// One category's row in the category dropdown.
/// </summary>
/// <remarks>
/// Checkable rather than a single selection, because the question is nearly always about two
/// of them at once: an export is <c>Sync</c> deciding and <c>Plc</c> doing, and reading either
/// on its own tells half the story.
/// </remarks>
public sealed class CategoryToggle(string name, bool enabled) : ObservableObject
{
    private bool _isEnabled = enabled;
    private int _count;

    public string Name { get; } = name;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// How many entries the buffer holds for this category.
    /// </summary>
    /// <remarks>
    /// Shown beside the name because the list is otherwise a wall of nine identical-looking
    /// options, and the counts are what tell somebody where the volume actually is — which is
    /// usually the reason the dropdown was opened.
    /// </remarks>
    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}

/// <summary>
/// What the log panel is currently showing, and what it is hiding.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="LogViewModel"/> so the filtering rules can be tested without a
/// dispatcher, a sink or a window — the view model owns batching and scrolling, which need
/// all three, and the predicate is the part with the edge cases in it.
/// </para>
/// <para>
/// <b>The categories are the ones the app can emit, listed up front</b>, rather than
/// discovered from entries as they arrive. A list that grows as the app runs would leave a
/// category missing from the dropdown precisely while nothing had happened in it yet — which
/// is when somebody would go looking for it to find out why.
/// </para>
/// </remarks>
public sealed class LogFilterViewModel : ObservableObject
{
    /// <summary>
    /// Every category in <see cref="LogCategory"/>, in the order they are worth reading.
    /// </summary>
    /// <remarks>
    /// Ordered by what somebody is usually chasing rather than alphabetically: the sync
    /// engine and the toolchain first, then git, then the supporting detail. Alphabetical
    /// would open the list on <c>App</c>, which is three lines a session.
    /// </remarks>
    private static readonly string[] KnownCategories =
    [
        LogCategory.Sync,
        LogCategory.Plc,
        CodesysLog.Category,
        TiaPortalLog.Category,
        LogCategory.Git,
        LogCategory.Lock,
        LogCategory.Watch,
        LogCategory.Fs,
        LogCategory.Ui,
        LogCategory.Perf,
        LogCategory.App,
    ];

    private string _text = string.Empty;
    private bool _batching;

    public LogFilterViewModel()
    {
        Levels =
        [
            new LevelToggle(LogLevel.Debug, "DBG", "Brush.Text.Disabled", enabled: true),
            new LevelToggle(LogLevel.Information, "INF", "Brush.Status.Neutral", enabled: true),
            new LevelToggle(LogLevel.Warning, "WRN", "Brush.Status.Modified", enabled: true),
            new LevelToggle(LogLevel.Error, "ERR", "Brush.Status.Deleted", enabled: true),
        ];

        Categories = [.. KnownCategories.Select(c => new CategoryToggle(c, enabled: true))];

        foreach (var toggle in Levels)
        {
            toggle.PropertyChanged += (_, _) => Raise();
        }

        foreach (var toggle in Categories)
        {
            toggle.PropertyChanged += (_, e) =>
            {
                // A count moving is not a filter change, and rebuilding the whole view on
                // every arriving entry would undo the batching the panel exists to have.
                if (e.PropertyName == nameof(CategoryToggle.IsEnabled))
                {
                    Raise();
                }
            };
        }

        // The level chips are ToggleButtons bound straight to IsEnabled, so there is no
        // command for them: a click already means "flip this one", and routing that through
        // a command with the toggle as its parameter adds a layer that can only agree.
        AllCategoriesCommand = new RelayCommand(() => SetAllCategories(true));
        NoCategoriesCommand = new RelayCommand(() => SetAllCategories(false));
    }

    public ObservableCollection<LevelToggle> Levels { get; }

    public ObservableCollection<CategoryToggle> Categories { get; }

    public RelayCommand AllCategoriesCommand { get; }

    public RelayCommand NoCategoriesCommand { get; }

    /// <summary>Free-text match over the message and the category.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                Raise();
            }
        }
    }

    /// <summary>
    /// What the category button says. Names the selection when it is short enough to name.
    /// </summary>
    /// <remarks>
    /// "All categories" and "3 of 10" are both more useful than a static "Categories" label,
    /// because the state that matters here is invisible once the dropdown closes — a panel
    /// filtered down to one category looks exactly like a quiet app.
    /// </remarks>
    public string CategorySummary
    {
        get
        {
            var on = Categories.Where(c => c.IsEnabled).ToList();

            return on.Count switch
            {
                0 => "No categories",
                1 => on[0].Name,
                var n when n == Categories.Count => "All categories",
                var n => $"{n} of {Categories.Count}",
            };
        }
    }

    /// <summary>True when anything at all is being held back, so the panel can say so.</summary>
    public bool IsFiltering =>
        Levels.Any(l => !l.IsEnabled) ||
        Categories.Any(c => !c.IsEnabled) ||
        _text.Length > 0;

    /// <summary>Raised when the predicate changes and the view has to be rebuilt.</summary>
    public event Action? Changed;

    /// <summary>Does this entry survive the current filter?</summary>
    public bool Passes(LogEvent entry)
    {
        if (!Levels.Any(l => l.Level == entry.Level && l.IsEnabled))
        {
            return false;
        }

        // An entry whose category is not in the known list is shown rather than hidden. The
        // alternative is that adding a category to LogCategory and forgetting it here makes
        // its entries silently unreachable — a filter that hides what nobody knows to look
        // for is the same failure as a comment claiming a file nobody wrote.
        var known = Categories.FirstOrDefault(
            c => string.Equals(c.Name, entry.Category, StringComparison.Ordinal));

        if (known is { IsEnabled: false })
        {
            return false;
        }

        return _text.Length == 0 ||
               entry.Message.Contains(_text, StringComparison.OrdinalIgnoreCase) ||
               entry.Category.Contains(_text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recount the per-category badges from whatever the buffer currently holds.</summary>
    public void UpdateCounts(IEnumerable<LogEvent> entries)
    {
        var counts = entries
            .GroupBy(e => e.Category, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var category in Categories)
        {
            category.Count = counts.GetValueOrDefault(category.Name);
        }
    }

    /// <summary>
    /// Show one category and hide the rest, at every level that can carry a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The levels are set as well as the categories</b>, or this keeps only half a promise.
    /// A caller asking for a category wants to see what is in it; landing on a panel that is
    /// still hiding <c>WRN</c> from a previous session would show a subset and look like the
    /// whole, which is the failure mode the panel's own <c>IsFiltering</c> line exists for.
    /// <c>DBG</c> is deliberately left as the caller found it — it is the one level that is
    /// volume rather than content, and turning it on unasked buries what was being looked for.
    /// </para>
    /// <para>
    /// <b>It stomps the current filter on purpose, and says so on screen.</b>
    /// <see cref="CategorySummary"/> drops to the category's own name and <see cref="IsFiltering"/>
    /// goes true, so the new state is visible and one click from "All categories" undoes it.
    /// The alternative — a filter that respects what was there — cannot guarantee the thing the
    /// caller asked to see is actually shown.
    /// </para>
    /// </remarks>
    public void ShowOnly(string category) => AsOneChange(() =>
    {
        foreach (var toggle in Categories)
        {
            toggle.IsEnabled = string.Equals(toggle.Name, category, StringComparison.Ordinal);
        }

        foreach (var level in Levels.Where(l => l.Level != LogLevel.Debug))
        {
            level.IsEnabled = true;
        }
    });

    private void SetAllCategories(bool enabled) => AsOneChange(() =>
    {
        foreach (var category in Categories)
        {
            category.IsEnabled = enabled;
        }
    });

    /// <summary>
    /// Run a batch of toggle changes and raise <see cref="Changed"/> once at the end.
    /// </summary>
    /// <remarks>
    /// <b>Every raise costs a full rebuild</b> — <c>LogViewModel.Rebuild</c> clears the bound
    /// collection and re-walks the whole sink, which holds up to ten thousand entries. Setting
    /// ten toggles in a loop therefore did ten of them, on the UI thread, inside a click. That
    /// "All categories" and "No categories" had it from the day they were written and nobody
    /// noticed, because a wasted rebuild leaves no trace. <see cref="ShowOnly"/> is the caller
    /// that made it worth fixing, and it fixed those two on the way.
    /// <para>
    /// A flag rather than a disposable scope: there is no re-entrancy to protect against — every
    /// caller is a synchronous loop over toggles on the dispatcher — and a scope would invite
    /// nesting that this does not handle.
    /// </para>
    /// </remarks>
    private void AsOneChange(Action changes)
    {
        _batching = true;

        try
        {
            changes();
        }
        finally
        {
            // In a finally so a throw mid-batch cannot leave the filter permanently mute,
            // which would be a panel that silently stops responding to its own controls.
            _batching = false;
        }

        Raise();
    }

    private void Raise()
    {
        if (_batching)
        {
            return;
        }

        OnPropertyChanged(nameof(CategorySummary));
        OnPropertyChanged(nameof(IsFiltering));
        Changed?.Invoke();
    }
}
