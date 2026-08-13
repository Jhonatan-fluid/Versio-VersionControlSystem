namespace Versio.Core.Watching;

/// <summary>What a filesystem event means for the git status the shell is displaying.</summary>
public enum RepositoryChangeRelevance
{
    /// <summary>Nothing git will report. Do not spend a <c>git status</c> on it.</summary>
    Ignored,

    /// <summary>A file in the working tree moved, changed or vanished.</summary>
    WorkingTree,

    /// <summary>
    /// Git's own state changed — someone staged, committed, checked out or merged, most
    /// likely in a terminal or another tool. The branch and ahead/behind counts move with
    /// it, so this refreshes more than the file list does.
    /// </summary>
    GitMetadata,
}

/// <summary>
/// Decides whether a filesystem event is worth re-running <c>git status</c> for.
/// </summary>
/// <remarks>
/// <para>
/// This is pure string work on purpose: it is the part most likely to be subtly wrong, and
/// getting it wrong is invisible. Filter too much and the panel goes stale — the bug this
/// exists to fix. Filter too little and the app spawns git in a loop.
/// </para>
/// <para>
/// <b>Working-tree paths are never filtered by name.</b> It is tempting to drop
/// <c>bin/</c>, <c>obj/</c> and <c>node_modules/</c> since a build writes thousands of
/// files, but whether those are ignored is a property of the repository's
/// <c>.gitignore</c>, not of their names — some repositories genuinely track a
/// <c>bin/</c> folder, and silently ignoring it would reintroduce exactly the staleness
/// being fixed. Volume is handled where it belongs, by debouncing: five thousand events
/// from one build still settle into a single refresh.
/// </para>
/// <para>
/// Inside <c>.git</c> the opposite applies. Git rewrites that directory constantly —
/// object files, logs, lock files — and almost none of it changes what the user sees.
/// Only the handful of entries that actually move the status are allowed through.
/// </para>
/// <para>
/// <b>The one exception to "never by name" is a path Versio itself put in
/// <c>.gitignore</c>.</b> That is not a guess about what the repository ignores — it is the
/// rule this app wrote, so a change there provably cannot alter what <c>git status</c>
/// prints. The case that forced it: CODESYS rewrites <c>&lt;project&gt;.~u</c> every few
/// seconds for as long as the IDE holds the project, so an engineer working normally produced
/// a <c>git status</c> every couple of seconds, forever, for a file git had been told to
/// ignore. Measured at roughly 24 git processes a minute.
/// </para>
/// </remarks>
public static class RepositoryChangeFilter
{
    /// <summary>
    /// Files directly under <c>.git</c> whose contents decide what <c>git status</c> prints.
    /// </summary>
    /// <remarks>
    /// The <c>*_HEAD</c> entries exist during a merge, rebase, cherry-pick or revert. They
    /// appear and disappear as the operation progresses, and each transition changes whether
    /// files are reported as conflicted — so the panel has to follow them.
    /// </remarks>
    private static readonly HashSet<string> InterestingGitFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "index",
        "HEAD",
        "ORIG_HEAD",
        "MERGE_HEAD",
        "MERGE_MSG",
        "CHERRY_PICK_HEAD",
        "REVERT_HEAD",
        "REBASE_HEAD",
        "packed-refs",
    };

    /// <summary>Classify one filesystem event.</summary>
    /// <param name="repositoryRoot">Working tree root.</param>
    /// <param name="fullPath">Absolute path the watcher reported.</param>
    /// <param name="knownIgnored">
    /// Repo-relative globs Versio itself wrote into <c>.gitignore</c> — the vendor's lock
    /// marker and its scratch files, from <c>IPlcProjectFormat.IgnorePatternsFor</c>. Empty
    /// means "filter nothing by name", which is the behaviour this had before and the right
    /// default for any caller that does not know the repository's bindings.
    /// </param>
    /// <remarks>
    /// <b>Only globs this app is responsible for.</b> Reading the repository's whole
    /// <c>.gitignore</c> and honouring it here would be the same feature and a much worse
    /// idea: git's ignore rules compose across files, directories and excludes, and a filter
    /// that got one of those wrong would make the panel silently stale — which is the bug
    /// this class exists to prevent, reintroduced in the name of performance.
    /// </remarks>
    /// <param name="directoryWrite">
    /// The OS reported a <i>write to a directory</i> rather than to a file. Windows raises one
    /// for the parent every time a child is created or deleted, and it carries no information
    /// about which child — the same shape as the bare <c>.git</c> event handled below, and
    /// nothing is lost by dropping it, because the child's own event arrives too.
    /// <para>
    /// This is what remained after the vendor's lock marker was filtered by name: CODESYS
    /// rewrites the marker through a transient sibling, so the marker events were ignored and
    /// the <i>folder's</i> mtime kept changing anyway, at exactly the same rate.
    /// </para>
    /// </param>
    public static RepositoryChangeRelevance Classify(
        string repositoryRoot,
        string fullPath,
        IReadOnlyList<string>? knownIgnored = null,
        bool directoryWrite = false)
    {
        var relative = ToRelative(repositoryRoot, fullPath);
        if (relative is null)
        {
            // Outside the tree. A watcher should never report this, but a junction or a
            // symlinked subfolder can produce it, and acting on it would refresh a
            // repository the path has nothing to do with.
            return RepositoryChangeRelevance.Ignored;
        }

        // Before the .git split, because these paths are in the working tree and the answer
        // is the same wherever they sit.
        if (knownIgnored is { Count: > 0 } && Bindings.PathGlob.Matches(relative, knownIgnored))
        {
            return RepositoryChangeRelevance.Ignored;
        }

        var firstSlash = relative.IndexOf('/');
        var firstSegment = firstSlash < 0 ? relative : relative[..firstSlash];

        if (!firstSegment.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            // A folder whose own timestamp moved because something inside it appeared or
            // vanished. The child's event says what; this one only says that something did.
            //
            // Dropped rather than debounced, because it is not noise from volume - it is a
            // duplicate of information already arriving by a better route. A creation, a
            // deletion or a rename of the FOLDER ITSELF is not a write and still gets through.
            return directoryWrite
                ? RepositoryChangeRelevance.Ignored
                : RepositoryChangeRelevance.WorkingTree;
        }

        // A bare ".git" event, with no entry named. Windows raises one of these for the
        // DIRECTORY every single time anything inside it is written - including the lock
        // files git creates and deletes around every invocation, and the index write that
        // `git status` itself performs. Measured: reacting to it made lock-file churn alone
        // wake the watcher.
        //
        // Nothing is lost by dropping it. A write to .git/index also raises an event naming
        // .git/index, which is matched below; this one carries no information about what
        // changed, only that something did.
        if (firstSlash < 0)
        {
            return RepositoryChangeRelevance.Ignored;
        }

        var inGit = relative[(firstSlash + 1)..];

        // index.lock and friends appear and vanish around every single git invocation,
        // including the status calls this app makes itself. Following them is a treadmill.
        if (inGit.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryChangeRelevance.Ignored;
        }

        // A branch moved, was created or was deleted.
        if (inGit.StartsWith("refs/", StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryChangeRelevance.GitMetadata;
        }

        return InterestingGitFiles.Contains(inGit)
            ? RepositoryChangeRelevance.GitMetadata
            : RepositoryChangeRelevance.Ignored;
    }

    /// <summary>
    /// Repository-relative path with forward slashes, or null when outside the tree.
    /// </summary>
    /// <remarks>
    /// Deliberately string-based rather than <c>Path.GetRelativePath</c>: this runs in
    /// <c>Versio.Core</c>, which does not touch the filesystem, and the comparison must not
    /// depend on whether either path currently exists — deletions are exactly the case that
    /// matters most and the file is gone by the time the event arrives.
    /// </remarks>
    public static string? ToRelative(string repositoryRoot, string fullPath)
    {
        var root = Normalize(repositoryRoot).TrimEnd('/');
        var path = Normalize(fullPath);

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (path.Length == root.Length)
        {
            return string.Empty;
        }

        // Guard against C:/repo matching C:/repository.
        if (path[root.Length] != '/')
        {
            return null;
        }

        return path[(root.Length + 1)..];
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
