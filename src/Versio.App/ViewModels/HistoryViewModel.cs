using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// A row under a commit that is not part of its tree: "reading…", why a commit lists nothing,
/// or why it could not be read.
/// </summary>
/// <remarks>
/// <para>
/// A row rather than a separate panel, because the tree needs <i>something</i> under a
/// collapsed commit for the expander to be drawn at all — and a placeholder that then says
/// what happened is more use than one that vanishes and leaves the row looking broken.
/// </para>
/// <para>
/// Until 2026-10-27 this type was also the file row — one per raw path a commit touched. A
/// commit's files are <see cref="TreeRow"/>s now, built from the commit's own manifest
/// (<see cref="CommitSnapshot"/>), so what is left here is the note.
/// </para>
/// </remarks>
public sealed class CommitFileRow : ObservableObject
{
    private bool _isSelected;

    public CommitFileRow(CommitRow commit, string note)
    {
        Commit = commit;
        Path = note;
        Glyph = " ";
        BrushKey = "Brush.Status.Neutral";
        IsNote = true;
    }

    public CommitRow Commit { get; }

    /// <summary>The note's text. Named for the template that draws it in the file column.</summary>
    public string Path { get; }

    public string Glyph { get; }

    public string BrushKey { get; }

    /// <summary>Always true: clicking a note shows nothing, because it names no file.</summary>
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

    /// <summary>Two-way with the container, so <see cref="HistoryViewModel.ClearSelection"/> can let go.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// One commit in the history panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its tree is read the first time it is expanded, and never again.</b> A commit is
/// immutable — its hash <i>is</i> its content — so the project snapshot behind one can be
/// held for the life of the session with no risk at all of going stale. That is what makes
/// caching safe here when the same trick would be wrong for the working tree.
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
    private readonly Func<CommitRow, Task> _loadTree;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _requested;

    public CommitRow(CommitEntry commit, Func<CommitRow, Task> loadTree)
    {
        Entry = commit;
        _loadTree = loadTree;

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

    /// <summary>Branches and tags on this commit. See <see cref="CommitEntry.Refs"/>.</summary>
    public IReadOnlyList<CommitRef> Refs => Entry.Refs;

    /// <summary>For the template: a row with no names draws no label strip at all.</summary>
    public bool HasRefs => Entry.Refs.Count > 0;

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

    /// <summary>Two-way with the container, so <see cref="HistoryViewModel.ClearSelection"/> can let go.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

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

    /// <summary>
    /// What this commit touched, as the project tree draws it, once somebody has asked.
    /// </summary>
    /// <remarks>
    /// <see cref="TreeRow"/>s for the tree and <see cref="CommitFileRow"/>s for the notes, so
    /// the collection is of <c>object</c>; the view picks a template by type. One list rather
    /// than two, because the tree needs one child under a collapsed commit for its expander,
    /// and that child is a note until the tree arrives.
    /// </remarks>
    public ObservableCollection<object> Children { get; } = [];

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
            _ = _loadTree(this);
        }
    }

    /// <summary>Say what is happening while the snapshot is read.</summary>
    /// <remarks>
    /// Replaces the seeded "…": a commit on a large export tree costs a handful of git spawns,
    /// and a row that sat on "…" for a second read as hung. The text names the commit so that
    /// an expanded row two screens down still says what it is waiting for.
    /// </remarks>
    public void ShowReading()
    {
        Children.Clear();
        Children.Add(new CommitFileRow(this, $"Reading the project as of {ShortId}…"));
    }

    /// <summary>Replace the placeholder with the commit's tree — or with why there is none.</summary>
    public void ShowTree(CommitSnapshot snapshot)
    {
        Children.Clear();

        if (snapshot.Changes.Count == 0)
        {
            // Nearly always a merge: git suppresses the combined diff, so there is no file
            // list to show. Saying which beats an empty branch that reads as a failure.
            Children.Add(new CommitFileRow(this,
                "No file list — this is usually a merge commit."));
            return;
        }

        // The roots expand by TreeRow's own rule - a project, or anything with changed
        // descendants opens itself - which for a changed-only tree is the path to every change.
        var context = new CommitTreeContext(this, snapshot);
        foreach (var root in snapshot.Nodes)
        {
            Children.Add(new TreeRow(root, 0, expansion: null, context));
        }

        // A manifest or catalog that could not be read is a row, not a silence: the tree above
        // it is then drawn from less than the commit holds, and the reader should know. Each
        // problem is in the log; one line here is enough to send them there.
        if (snapshot.Repo.Problems.Count > 0)
        {
            Children.Add(new CommitFileRow(this, snapshot.Repo.Problems.Count == 1
                ? "1 file of this commit could not be read as a project — the log says which."
                : $"{snapshot.Repo.Problems.Count} files of this commit could not be read as a project — the log says which."));
        }
    }

    /// <summary>The snapshot could not be read at all; say so, and let the next expand try again.</summary>
    /// <remarks>
    /// Before this a failed read left "…" under the commit for the rest of the session — the
    /// shape of a hang, with the one row that could explain it saying nothing. Re-armed rather
    /// than kept, because the cause is nearly always of the moment (git busy, a lock held).
    /// </remarks>
    public void ShowFailure(string why)
    {
        Children.Clear();
        Children.Add(new CommitFileRow(this, $"Could not read this commit: {why}"));
        _requested = false;
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
    /// Snapshots already read, or being read, by commit id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Survives a reload of the list, because a commit's contents cannot change underneath it.
    /// A refresh after a commit therefore costs one <c>git log</c> and no <c>git show</c> at
    /// all for anything the user had already opened.
    /// </para>
    /// <para>
    /// <b>The task, not the result</b>, so that two askers for one commit share one read: a
    /// click on the commit card wants its index for the heading while the expand is still
    /// reading the same manifests (the web viewer's single-flight argument, in one line). A
    /// task that failed is taken out again by <see cref="SnapshotAsync"/>, so a cause of the
    /// moment is not remembered as the answer.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, Task<CommitSnapshot>> _snapshots = [];

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

    // ------------------------------------------- find in what is loaded (UX-6)

    private string _filter = string.Empty;

    /// <summary>Narrow the loaded commits by subject or author.</summary>
    /// <remarks>
    /// <para>
    /// <b>Over what is loaded, and only what is loaded.</b> This narrows the commits already
    /// read; "Load older commits" keeps walking the same unfiltered history a page at a time,
    /// because <see cref="LoadMoreAsync"/> skips by <c>Commits.Count</c> and the filter lives
    /// on the view, never on the collection. Filtering the walk itself would turn a box meant
    /// for "where is that commit I scrolled past" into an unbounded <c>git log</c> over the
    /// whole branch — the eager cost this whole class exists to avoid.
    /// </para>
    /// <para>
    /// Subject and author, deliberately not path: per-file history is out of scope and stated
    /// as such in the phase 1 close, and a box that filtered <i>loaded</i> commits by path
    /// would look exactly like the feature that was declined while doing something much
    /// weaker — matching only whichever commits happen to be in memory.
    /// </para>
    /// </remarks>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>The filter matched nothing in what is loaded, said where the rows were.</summary>
    /// <remarks>
    /// A tree emptied by the filter reads as "no history", which is wrong twice: the loaded
    /// commits are all still there, and the branch may hold a match further back that simply
    /// has not been loaded — which is why the line bound to this mentions loading older
    /// commits rather than pronouncing the search over.
    /// </remarks>
    public bool FilterHidesEverything =>
        Commits.Count > 0 &&
        !string.IsNullOrWhiteSpace(_filter) &&
        !Commits.Any(MatchesFilter);

    private void ApplyFilter()
    {
        // The default view is what the TreeView renders when it binds Commits directly, so
        // the veil needs no second collection — and a row's loaded children and file cache
        // survive being hidden, because nothing is removed.
        var view = CollectionViewSource.GetDefaultView(Commits);
        view.Filter = string.IsNullOrWhiteSpace(_filter)
            ? null
            : o => o is CommitRow row && MatchesFilter(row);
        OnPropertyChanged(nameof(FilterHidesEverything));
    }

    private bool MatchesFilter(CommitRow row)
    {
        var needle = _filter.Trim();
        return needle.Length == 0
            || row.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || row.Author.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read the next page back. Enabled only when there is one.</summary>
    public AsyncCommand LoadMoreCommand { get; }

    /// <summary>Raised when a commit row is picked, for the shell to render its patch.</summary>
    public event Action<CommitRow>? CommitSelected;

    /// <summary>Raised when a row of a commit's tree is picked — an object, a file, a project.</summary>
    /// <remarks>
    /// The row carries its commit in <see cref="TreeRow.Context"/> (a <see cref="CommitTreeContext"/>),
    /// which is how the shell knows to read the file at that commit and not from disk.
    /// </remarks>
    public event Action<TreeRow>? TreeSelected;

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
                Commits.Add(new CommitRow(commit, LoadTreeAsync)
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

    /// <summary>Read one commit's tree into its row, once.</summary>
    private async Task LoadTreeAsync(CommitRow row)
    {
        if (_git is null)
        {
            return;
        }

        row.ShowReading();
        try
        {
            row.ShowTree(await SnapshotAsync(row.Id, row.ShortId).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            // Said twice on purpose: in the log for whoever reads it later, and on the row for
            // whoever is looking at it now. The old code let the exception go, which left "…"
            // under the commit for good.
            _logger.Error(LogCategory.Git, $"history: could not read {row.ShortId} as a project tree", ex);
            row.ShowFailure(ex.Message);
        }
    }

    /// <summary>
    /// The commit read as a project, shared by everyone who asks for that commit.
    /// </summary>
    /// <remarks>
    /// Public because the shell asks too: the commit heading counts the commit's objects by
    /// the commit's own index (<see cref="CommitSnapshot.Objects"/>), and a click on the card
    /// must not read the manifests a second time while the expand is reading them.
    /// </remarks>
    public async Task<CommitSnapshot> SnapshotAsync(string commitId, string shortId)
    {
        if (_git is not { } git)
        {
            throw new InvalidOperationException("No repository is attached to the history.");
        }

        if (!_snapshots.TryGetValue(commitId, out var task))
        {
            task = BuildSnapshotAsync(git, commitId, shortId);
            _snapshots[commitId] = task;
        }

        try
        {
            return await task.ConfigureAwait(true);
        }
        catch
        {
            // Only this read's own entry: a later successful read of the same commit must not
            // be thrown away by a failure that finished after it.
            if (_snapshots.TryGetValue(commitId, out var held) && ReferenceEquals(held, task))
            {
                _snapshots.Remove(commitId);
            }

            throw;
        }
    }

    /// <summary>
    /// Read a commit as a project: its tree, its changes, and its manifests as they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The call budget is the point, and the log line at the end reports it: one
    /// <c>ls-tree</c>, one <c>--name-status</c>, then one <c>show</c> per file
    /// <see cref="ProjectSnapshotLoader"/> asks for — <c>repo.json</c>, each binding's manifest,
    /// each project's catalogs — plus the parent's manifest for each binding the commit deleted
    /// an object from. Nothing per changed file, nothing per object: a commit touching four
    /// hundred exported files costs the same handful of spawns as one touching one. A
    /// <c>cat-file --batch</c> would be fewer still, and was rejected: its size-framed output
    /// cannot survive the CLI runner's line reassembly (see <c>ShowAtCommitAsync</c>'s remark).
    /// </para>
    /// <para>
    /// <b>Changed-only.</b> The question a commit answers is "what did this touch", so the tree
    /// holds the changed objects and the folders and projects above them. The whole project as
    /// of the commit is the same snapshot with the option off, and is one toggle away when
    /// somebody asks for it.
    /// </para>
    /// <para>
    /// Every path read came from the commit's own listing, so an empty answer from
    /// <c>git show</c> is an empty file, and git's own failure is already a line in the log
    /// (the runner logs every command). The loader is handed the text as text.
    /// </para>
    /// </remarks>
    private async Task<CommitSnapshot> BuildSnapshotAsync(IGitClient git, string commitId, string shortId)
    {
        _logger.Info(LogCategory.Git, $"history: reading {shortId} as a project tree");
        var watch = Stopwatch.StartNew();

        var files = await git.ListTreeAsync(commitId, CancellationToken.None).ConfigureAwait(true);
        var changes = await git.CommitFilesAsync(commitId, CancellationToken.None).ConfigureAwait(true);

        var reads = 0;
        async Task<SnapshotRead> Read(string path)
        {
            reads++;
            return SnapshotRead.Of(await git.ShowAtCommitAsync(commitId, path, CancellationToken.None).ConfigureAwait(true));
        }

        var repo = await ProjectSnapshotLoader.LoadAsync(
            files, Read, $"as of {shortId}", _logger, LogCategory.Git, shortId, CancellationToken.None).ConfigureAwait(true);

        // What this commit deleted - and the old place of what it renamed - is no longer in its
        // manifest, so the parent's manifest names those rows (the loader has the reasoning).
        // One read per binding with a deletion, none otherwise; the parent of a root commit
        // has nothing, which reads as absent.
        async Task<SnapshotRead> ReadParent(string path)
        {
            reads++;
            var text = await git.ShowAtCommitAsync(commitId + "^", path, CancellationToken.None).ConfigureAwait(true);
            return text.Length == 0 ? SnapshotRead.Absent : SnapshotRead.Of(text);
        }

        var gone = changes.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path)
            .Concat(changes.Where(c => c.OriginalPath is { Length: > 0 }).Select(c => c.OriginalPath!))
            .ToList();
        repo = await ProjectSnapshotLoader.WithDeletedObjectsAsync(
            repo, gone, ReadParent, _logger, LogCategory.Git, shortId, CancellationToken.None).ConfigureAwait(true);

        // A commit has one side: what it did to each path is its "staged" state, and nothing
        // is unstaged. The same mapping the web viewer's marked tree uses.
        var marks = changes.Select(c => new FileChange
        {
            Path = c.Path,
            Staged = c.Kind,
            Unstaged = ChangeKind.Unmodified,
            OriginalPath = c.OriginalPath,
            SimilarityScore = c.SimilarityScore,
        }).ToList();
        var nodes = RepoExplorerBuilder.Build(
            files, repo.Projects,
            new ProjectTreeOptions { ChangedOnly = true, HideUnchangedProjects = true },
            changes: marks);

        var gitCalls = 2 + reads;
        foreach (var problem in repo.Problems)
        {
            _logger.Warn(LogCategory.Git, $"history: {shortId}: {problem}");
        }

        _logger.Info(LogCategory.Git,
            $"history: {shortId}: {files.Count} files, {repo.Projects.Count} project(s), {changes.Count} changed, "
            + $"{repo.Problems.Count} problem(s), {gitCalls} git calls in {watch.ElapsedMilliseconds} ms");

        return new CommitSnapshot(commitId, shortId, repo, changes, nodes, gitCalls);
    }

    /// <summary>The row last handed to <see cref="Select"/>, kept so it can be let go of.</summary>
    private object? _selected;

    /// <summary>A row was picked in the tree. A commit and a file render differently.</summary>
    public void Select(object? row)
    {
        _selected = row;

        switch (row)
        {
            // A folder groups; it has no file of its own to show. A note names nothing.
            case TreeRow { IsFolder: false } tree:
                TreeSelected?.Invoke(tree);
                break;

            case CommitRow commit:
                CommitSelected?.Invoke(commit);
                break;
        }
    }

    /// <summary>Drop the selection, so the same row can be picked again.</summary>
    /// <remarks>
    /// The shell calls this when the rail leaves the history, having emptied the pane this
    /// panel fed. A row still highlighted in the tree raises nothing when it is clicked again,
    /// so without this the pane would stay blank on the way back — the same reason
    /// <c>TreeRow.IsSelected</c> exists for the other two trees.
    /// </remarks>
    public void ClearSelection()
    {
        switch (_selected)
        {
            case TreeRow tree:
                tree.IsSelected = false;
                break;

            case CommitFileRow note:
                note.IsSelected = false;
                break;

            case CommitRow commit:
                commit.IsSelected = false;
                break;
        }

        _selected = null;
    }

    private void Reset()
    {
        _selected = null;
        Commits.Clear();
        _snapshots.Clear();
        _stale = true;
        HasMore = false;

        // The filter is about a history that is no longer loaded; carrying it over would
        // open the next repository on a tree mysteriously showing nothing.
        Filter = string.Empty;

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
        // Loading a page or clearing the list changes what the filter has to match against;
        // this is the one funnel every one of those paths already goes through.
        OnPropertyChanged(nameof(FilterHidesEverything));

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
