using System.Collections.ObjectModel;
using System.Globalization;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;

namespace Versio.App.ViewModels;

/// <summary>One file inside an expanded commit.</summary>
/// <remarks>
/// Carries the commit it came from, because that is what the diff needs: the same path exists
/// in every commit that ever touched it, and a row that knew only its path could not say
/// which of those changes to render.
/// </remarks>
public sealed class CommitFileRow
{
    /// <summary>A real file row.</summary>
    public CommitFileRow(CommitRow commit, CommitFile file)
    {
        Commit = commit;
        Path = file.Path;
        Glyph = ChangeGlyph.For(file.Kind);
        BrushKey = ChangeGlyph.BrushFor(file.Kind);
        OriginalPath = file.OriginalPath;
    }

    /// <summary>
    /// A row that is not a file: "loading…", or why a commit lists none.
    /// </summary>
    /// <remarks>
    /// A row rather than a separate panel, because the tree needs <i>something</i> under a
    /// collapsed commit for the expander to be drawn at all — and a placeholder that then says
    /// what happened is more use than one that vanishes and leaves the row looking broken.
    /// </remarks>
    public CommitFileRow(CommitRow commit, string note)
    {
        Commit = commit;
        Path = note;
        Glyph = " ";
        BrushKey = "Brush.Status.Neutral";
        IsNote = true;
    }

    public CommitRow Commit { get; }

    /// <summary>The path, or the note's text — whichever this row is.</summary>
    public string Path { get; }

    public string Glyph { get; }

    public string BrushKey { get; }

    /// <summary>Where a renamed or copied file came from, otherwise null.</summary>
    public string? OriginalPath { get; }

    /// <summary>True for a placeholder. Clicking one shows nothing, because it names no file.</summary>
    public bool IsNote { get; }

    /// <summary>
    /// Always false: a file is a leaf.
    /// </summary>
    /// <remarks>
    /// Here so that one container style can serve both levels of the tree. Without it every
    /// file row binds <c>IsExpanded</c> against a property that does not exist and WPF logs a
    /// binding failure for each one — hundreds of them on a commit that touched an export
    /// tree, in the log somebody is reading to find a real problem.
    /// </remarks>
    public bool IsExpanded { get; set; }
}

/// <summary>
/// One commit in the history panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its files are fetched the first time it is expanded, and never again.</b> A commit is
/// immutable — its hash <i>is</i> its content — so the list of files behind one can be held
/// for the life of the session with no risk at all of going stale. That is what makes caching
/// safe here when the same trick would be wrong for the working tree.
/// </para>
/// <para>
/// The patches themselves are deliberately <i>not</i> held. A commit that touches an exported
/// CODESYS tree carries hundreds of them, and keeping every patch somebody scrolled past
/// would turn a read through the history into a memory leak. The diff pane holds the one on
/// screen, which is the only one anybody is reading.
/// </para>
/// </remarks>
public sealed class CommitRow : ObservableObject
{
    private readonly Func<CommitRow, Task> _loadFiles;
    private bool _isExpanded;
    private bool _requested;

    public CommitRow(CommitEntry commit, Func<CommitRow, Task> loadFiles)
    {
        Entry = commit;
        _loadFiles = loadFiles;

        // Seeded so the expander exists before anything is known about the commit's contents.
        // With no child there is no chevron, and with no chevron there is nothing to click to
        // find out what is underneath.
        Children.Add(new CommitFileRow(this, "…"));
    }

    public CommitEntry Entry { get; }

    public string Id => Entry.Id;

    public string ShortId => Entry.ShortId;

    public string Subject => Entry.Subject;

    public string Author => Entry.Author;

    /// <summary>
    /// When the commit was authored, in the reader's own timezone.
    /// </summary>
    /// <remarks>
    /// Local rather than UTC, for the same reason the log panel shows local times: the person
    /// reading it is correlating a commit with their own afternoon. Absolute rather than
    /// relative, with git's "2 hours ago" kept beside it — the relative form is what makes a
    /// list scannable, and this is what somebody quotes.
    /// </remarks>
    public string When => Entry.When is { } when
        ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : string.Empty;

    public string Age => Entry.Age;

    private bool _isUnpushed;

    /// <summary>
    /// True while no remote has this commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The history panel listed every commit identically, so there was nothing on screen
    /// separating work that exists in one place from work that is backed up and visible to
    /// everybody else. The branch badge says "2 ahead"; it does not say <i>which two</i>, and
    /// on a branch with no upstream it has nothing to count against at all.
    /// </para>
    /// <para>
    /// Settable, because it is the one thing about a commit that <b>does</b> change under it.
    /// The rest of a commit is immutable — its hash is its content, which is what makes the
    /// file cache safe — but whether a remote has it changes on every push and every fetch.
    /// </para>
    /// </remarks>
    public bool IsUnpushed
    {
        get => _isUnpushed;
        set => SetProperty(ref _isUnpushed, value);
    }

    /// <summary>The files this commit touched, once somebody has asked for them.</summary>
    public ObservableCollection<CommitFileRow> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value) || !value || _requested)
            {
                return;
            }

            // Once per row, not once per expand: collapsing a commit and opening it again
            // must not spawn a second `git show` for an answer that cannot have changed.
            _requested = true;
            _ = _loadFiles(this);
        }
    }

    /// <summary>Replace the placeholder with what git said — or with why it said nothing.</summary>
    public void ShowFiles(IReadOnlyList<CommitFile> files)
    {
        Children.Clear();

        if (files.Count == 0)
        {
            // Nearly always a merge: git suppresses the combined diff, so there is no file
            // list to show. Saying which beats an empty branch that reads as a failure.
            Children.Add(new CommitFileRow(this,
                "No file list — this is usually a merge commit."));
            return;
        }

        foreach (var file in files)
        {
            Children.Add(new CommitFileRow(this, file));
        }
    }
}

/// <summary>
/// The commit history, as its own panel.
/// </summary>
/// <remarks>
/// <para>
/// This used to be a 150-pixel list wedged under the stash in the git panel, reloaded on every
/// refresh that touched git's metadata whether or not anybody was looking at it. Everything
/// here is arranged so that nothing is read until somebody asks for it, at four separate
/// levels: <b>no history at all</b> until the panel is first shown, <b>one page</b> at a time
/// after that, <b>a commit's files</b> only when it is expanded, and <b>a file's patch</b>
/// only when it is clicked.
/// </para>
/// <para>
/// The panel does not own the diff pane; it raises a selection and the shell renders it. One
/// pane, whichever list the row was reached from — which is the reason a change reads the same
/// whether it is on disk, in the index, or already committed.
/// </para>
/// </remarks>
public sealed class HistoryViewModel : ObservableObject
{
    /// <summary>How many commits one page holds.</summary>
    /// <remarks>
    /// Enough to cover the common case — glancing at what happened this week — in a single
    /// read, and small enough that the read is one fast <c>git log</c> rather than a walk of
    /// the whole branch.
    /// </remarks>
    public const int PageSize = 50;

    /// <summary>
    /// File lists already fetched, by commit id.
    /// </summary>
    /// <remarks>
    /// Survives a reload of the list, because a commit's contents cannot change underneath it.
    /// A refresh after a commit therefore costs one <c>git log</c> and no <c>git show</c> at
    /// all for anything the user had already opened.
    /// </remarks>
    private readonly Dictionary<string, IReadOnlyList<CommitFile>> _files = [];

    private readonly VersioLogger _logger;

    private IGitClient? _git;

    /// <summary>
    /// Which commits no remote has, as of the last read.
    /// </summary>
    /// <remarks>
    /// Read once per refresh rather than per page: it is one <c>rev-list</c>, and asking again
    /// for page four would let two pages of the same list disagree about the same commit.
    /// </remarks>
    private IReadOnlySet<string> _unpushed = new HashSet<string>(StringComparer.Ordinal);

    private bool _isVisible;
    private bool _stale = true;
    private bool _hasMore;
    private bool _isLoading;
    private string _summary = string.Empty;

    public HistoryViewModel(VersioLogger logger, Action<Exception>? onError = null)
    {
        _logger = logger;
        // Without onError an exception from git log is swallowed by AsyncCommand.Execute —
        // the catch only calls onError?.Invoke — so "Load older commits" failing looks like
        // a dead button. The shell passes ReportError; tests that do not care about the
        // failure path leave it null.
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => HasMore && !IsLoading, onError);
    }

    /// <summary>The commits currently loaded, newest first.</summary>
    public ObservableCollection<CommitRow> Commits { get; } = [];

    /// <summary>Read the next page back. Enabled only when there is one.</summary>
    public AsyncCommand LoadMoreCommand { get; }

    /// <summary>Raised when a commit row is picked, for the shell to render its patch.</summary>
    public event Action<CommitRow>? CommitSelected;

    /// <summary>Raised when a file inside a commit is picked.</summary>
    public event Action<CommitFileRow>? FileSelected;

    /// <summary>One line under the list: how much of the history is loaded.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Whether another page exists, as far as the last read could tell.</summary>
    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value))
            {
                LoadMoreCommand.RaiseCanExecuteChanged();
                UpdateSummary();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                LoadMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether the panel is the one the rail is showing.
    /// </summary>
    /// <remarks>
    /// This is the switch that keeps the first <c>git log</c> from running at all in a session
    /// where nobody opens the history — and, on a repository with a long log, the difference
    /// between opening instantly and waiting on a walk somebody never asked for.
    /// </remarks>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value) && value)
            {
                _ = RefreshIfNeededAsync();
            }
        }
    }

    /// <summary>Begin following a repository. Reads nothing yet.</summary>
    public void Attach(IGitClient git)
    {
        _git = git;
        Reset();

        // A repository that was opened while the history panel is already in front still has
        // to fill it. Attaching is the moment that becomes possible.
        if (IsVisible)
        {
            _ = RefreshIfNeededAsync();
        }
    }

    public void Detach()
    {
        _git = null;
        Reset();
    }

    /// <summary>
    /// Git's state moved, so what is loaded may now be wrong.
    /// </summary>
    /// <remarks>
    /// Marks rather than reloads. A commit, a checkout or a fetch can happen while the user is
    /// in the project panel, and re-reading the log for a panel nobody is looking at is the
    /// eager cost this class exists to avoid. When the panel <i>is</i> in front it reloads at
    /// once — a commit that did not appear in the history the moment it was made would look
    /// like it had failed.
    /// </remarks>
    public void Invalidate()
    {
        _stale = true;

        if (IsVisible)
        {
            _ = RefreshIfNeededAsync();
        }
    }

    /// <summary>Reload the first page, if anything has happened since the last one.</summary>
    private async Task RefreshIfNeededAsync()
    {
        if (!_stale || _git is null || IsLoading)
        {
            return;
        }

        _stale = false;
        Commits.Clear();

        // Back to the first page rather than re-reading every page that had been opened. The
        // trigger is nearly always a commit the user has just made, and that one is at the top.
        await LoadPageAsync(skip: 0).ConfigureAwait(true);
    }

    private Task LoadMoreAsync() => LoadPageAsync(Commits.Count);

    private async Task LoadPageAsync(int skip)
    {
        if (_git is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            // Before the page, and only for the first one. A later page is older history and
            // cannot contain a commit the first read would have missed, so re-asking would be
            // a second walk for an answer that has not changed - and would let two pages
            // disagree if a push landed between them.
            if (skip == 0)
            {
                _unpushed = await _git.UnpushedCommitIdsAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }

            var page = await _git.RecentCommitsAsync(PageSize, skip, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var commit in page)
            {
                Commits.Add(new CommitRow(commit, LoadFilesAsync)
                {
                    IsUnpushed = _unpushed.Contains(commit.Id),
                });
            }

            // A full page means there is probably another; a short one means the branch ran
            // out. Asking git for an exact total would be a second walk of the whole log to
            // decide whether to draw one button.
            HasMore = page.Count == PageSize;
            UpdateSummary();

            _logger.Debug(LogCategory.Git,
                $"history: read {page.Count} commits from {skip}, " +
                $"{_unpushed.Count} unpushed on this branch");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Fetch one commit's file list, once.</summary>
    private async Task LoadFilesAsync(CommitRow row)
    {
        if (_git is null)
        {
            return;
        }

        if (_files.TryGetValue(row.Id, out var cached))
        {
            row.ShowFiles(cached);
            return;
        }

        var files = await _git.CommitFilesAsync(row.Id, CancellationToken.None)
            .ConfigureAwait(true);

        _files[row.Id] = files;
        row.ShowFiles(files);

        _logger.Debug(LogCategory.Git, $"history: {files.Count} files in {row.ShortId}");
    }

    /// <summary>A row was picked in the tree. A commit and a file render differently.</summary>
    public void Select(object? row)
    {
        switch (row)
        {
            case CommitFileRow { IsNote: false } file:
                FileSelected?.Invoke(file);
                break;

            case CommitRow commit:
                CommitSelected?.Invoke(commit);
                break;
        }
    }

    private void Reset()
    {
        Commits.Clear();
        _files.Clear();
        _stale = true;
        HasMore = false;
        UpdateSummary();
    }

    /// <summary>
    /// How much of the history is loaded, and how much of it nobody else can see.
    /// </summary>
    /// <remarks>
    /// The unpushed count leads when there is one, because it is the actionable half — "23
    /// commits loaded" is context, "3 not pushed" is a thing to do something about. Counted
    /// across the whole set rather than the loaded page: a commit that has scrolled off the
    /// end of what is loaded is no more pushed for being out of sight.
    /// </remarks>
    private void UpdateSummary()
    {
        var loaded = Commits.Count switch
        {
            0 => string.Empty,
            1 => "1 commit loaded",
            var n => $"{n} commits loaded" + (HasMore ? ", more further back" : string.Empty),
        };

        if (loaded.Length == 0 || _unpushed.Count == 0)
        {
            Summary = loaded;
            return;
        }

        Summary = _unpushed.Count == 1
            ? $"1 commit not pushed · {loaded}"
            : $"{_unpushed.Count} commits not pushed · {loaded}";
    }
}
