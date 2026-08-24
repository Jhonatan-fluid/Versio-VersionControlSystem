using Versio.Core.Git;

namespace Versio.Core.Abstractions;

/// <summary>
/// Everything Versio needs from git.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design revision (Rev 9): implemented entirely over <c>git.exe</c>, not LibGit2Sharp.</b>
/// The plan originally specified a hybrid — LibGit2Sharp for reads, the CLI for network
/// operations. Building it, three things argued against the split:
/// </para>
/// <list type="number">
///   <item><b>It removes R19 outright.</b> LibGit2Sharp carries native <c>git2-*.dll</c>
///     binaries, which are the documented way <c>PublishSingleFile</c> breaks. A pure-CLI
///     client has no native payload at all.</item>
///   <item><b>git.exe is already a hard dependency.</b> Credentials, LFS and network
///     operations all go through the engineer's own git regardless, so the CLI costs nothing
///     extra while LibGit2Sharp adds a second thing to keep in step with it.
///     <para>
///     This item used to claim PortableGit was <i>vendored</i> for those. It is not, and never
///     was: <see cref="GitProbe"/> resolves <c>git</c> from <c>PATH</c>, which is why a machine
///     without it gets an explained empty state rather than a working application. Vendoring
///     remains an option and would not change this interface; the sentence was corrected
///     rather than the code, because a doc comment asserting a dependency that does not exist
///     is the same defect class as a comment claiming a rule nothing enforces.
///     </para></item>
///   <item><b>Porcelain v2 is a stable machine format</b>, explicitly guaranteed across
///     versions. Parsing it is a bounded, testable problem — and it is now covered by
///     unit tests over real output shapes, which is more verification than the
///     LibGit2Sharp path would have had.</item>
/// </list>
/// <para>
/// The cost is a process spawn per status refresh (~20–50 ms). That is irrelevant next to
/// a 17–31 s CODESYS export, and the debounced refresh means it does not run in a loop.
/// If it ever becomes a problem, this interface is the seam to swap behind.
/// </para>
/// </remarks>
public interface IGitClient
{
    Task<bool> IsRepositoryAsync(CancellationToken ct);

    /// <summary>The version of git that will actually run, in its own words.</summary>
    /// <remarks>
    /// Asked of git rather than read from a setting, because the one on PATH is the one every
    /// other call here will use - and "which git is it using" is a question that has caused
    /// real confusion on machines carrying several.
    /// </remarks>
    Task<string> VersionAsync(CancellationToken ct);

    /// <summary>
    /// Who git will attribute a commit in this repository to.
    /// </summary>
    /// <remarks>
    /// Read at repository open rather than discovered at commit time. A commit with no identity
    /// fails with a lecture from git — and in Versio it fails <i>after</i> the export gate has
    /// run a 17–31 second CODESYS export, because the gate goes first so the mirror is never
    /// committed stale. Two <c>git config --get</c> calls remove that entirely.
    /// <para>
    /// Resolved the way git itself resolves it, so a value inherited from the global or system
    /// config counts as present: what matters is whether the commit will succeed, not where the
    /// setting was written.
    /// </para>
    /// </remarks>
    Task<GitIdentity> IdentityAsync(CancellationToken ct);

    /// <summary>
    /// Write a name and email into git's configuration.
    /// </summary>
    /// <param name="global">
    /// Write to the user's global config rather than this repository's. <b>Local is the
    /// default</b>, because a work machine carrying a personal global identity is a common and
    /// annoying way to attribute commits to the wrong person, and the repository in front of
    /// the user is the one they are answering about.
    /// </param>
    Task<GitResult> SetIdentityAsync(string name, string email, bool global, CancellationToken ct);

    Task<GitStatus> StatusAsync(CancellationToken ct);

    /// <summary>
    /// Every tracked and untracked path git will show, gitignored files excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>git ls-files -z -c -o --exclude-standard</c>. This is a repository listing, not a
    /// disk walk: <c>bin/</c>, <c>obj/</c> and <c>.versio/local/</c> stay out because git
    /// already ignores them, and <c>.git</c> is never a path git reports.
    /// </para>
    /// <para>
    /// NUL-separated for the same reason status is: CODESYS object names contain spaces and
    /// non-ASCII, and git quotes those without <c>-z</c>.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> ListWorkingTreeAsync(CancellationToken ct);

    Task<IReadOnlyList<BranchInfo>> BranchesAsync(CancellationToken ct);

    Task<GitResult> StageAsync(IReadOnlyList<string> paths, CancellationToken ct);

    Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct);

    Task<GitResult> CommitAsync(string message, CancellationToken ct);

    Task<GitResult> DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct);

    Task<GitResult> CreateBranchAsync(string name, bool checkout, CancellationToken ct);

    Task<GitResult> CheckoutAsync(string branch, CancellationToken ct);

    /// <summary>
    /// Whether a credential is already stored for this host, without asking for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>git credential fill</c> in a mode that cannot prompt. It consults the configured
    /// helper and answers from what is already there, so the panel can say "sign in needed"
    /// when a repository opens rather than waiting for the first push to fail.
    /// </para>
    /// <para>
    /// <b>The value is never returned.</b> Only whether one exists. Versio has no reason to
    /// hold a secret and every reason not to: the whole point of driving <c>git.exe</c> is
    /// that the credential lives where the engineer's own terminal keeps it.
    /// </para>
    /// </remarks>
    Task<bool> HasStoredCredentialAsync(string protocol, string host, CancellationToken ct);

    /// <summary>
    /// Ask the configured credential helper to obtain a credential, prompting if it must.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through <c>git credential fill</c> rather than by launching a helper directly.</b>
    /// Git invokes whatever is configured — Git Credential Manager, a corporate helper, a
    /// bare <c>store</c> — and that helper does its own thing: a window, a browser for OAuth,
    /// a token prompt. Shelling <c>git-credential-manager</c> by name would be brittle and
    /// would break the moment somebody uses a different helper.
    /// </para>
    /// <para>
    /// This is the <b>only</b> call in the application permitted to let git prompt, because it
    /// is the only one a user pressed a button to start.
    /// </para>
    /// </remarks>
    Task<GitResult> SignInAsync(string protocol, string host, CancellationToken ct);

    /// <summary>
    /// Erase the stored credential for a host.
    /// </summary>
    /// <remarks>
    /// <c>git credential reject</c>. The genuinely maddening failure is not a missing
    /// credential but a <i>stale</i> one — an expired token the helper keeps cheerfully
    /// returning, so every push fails identically and nothing the user does from inside the
    /// application changes it. This is the button that fixes that, and no git GUI offers it.
    /// </remarks>
    Task<GitResult> SignOutAsync(string protocol, string host, CancellationToken ct);

    /// <summary>
    /// Ask the remote what a branch currently points at, transferring no objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>git ls-remote</c>. This is the cheap half of keeping the ahead/behind counts honest:
    /// one line back and no object transfer, against a fetch's connection, negotiation and
    /// download. A real fetch runs only when this says the remote has actually moved.
    /// </para>
    /// <para>
    /// The answer distinguishes "the branch is not there" from "the remote did not answer" —
    /// see <see cref="RemoteBranchProbe"/> for why a string could not.
    /// </para>
    /// </remarks>
    Task<RemoteBranchProbe> LsRemoteAsync(string remote, string branch, CancellationToken ct);

    /// <summary>
    /// Resolve a revision to a commit id, locally.
    /// </summary>
    /// <remarks>
    /// The other half of the probe, and the reason it is cheap: comparing the remote's answer
    /// against the local remote-tracking ref is a few milliseconds of local reading. Empty when
    /// the revision does not resolve, which is ordinary for a branch with no upstream yet.
    /// </remarks>
    Task<string> RevParseAsync(string revision, CancellationToken ct);

    /// <summary>
    /// The content of a file as HEAD has it, or empty when HEAD has no such file.
    /// </summary>
    /// <param name="path">Repository-relative, forward slashes — git's own path shape.</param>
    /// <remarks>
    /// <para>
    /// <b>The only way to see a file the working tree no longer has.</b> Planning a rename needs
    /// the departed file's content, and the manifest keeps one hash per file rather than the
    /// bytes — so for anything a hash cannot answer, this is the source.
    /// </para>
    /// <para>
    /// <b>Empty for a mirror that was never committed</b>, and that is a real state rather than
    /// an error: an export that has not been committed yet has no HEAD copy of anything. The
    /// caller falls back to whatever it does without old content, which for a rename is a
    /// delete and a create.
    /// </para>
    /// </remarks>
    Task<string> ShowAtHeadAsync(string path, CancellationToken ct);

    /// <summary>
    /// Take one side of a conflicted file and stage it.
    /// </summary>
    /// <param name="ours">
    /// True for the version already on this branch, false for the one arriving. <b>"Ours" and
    /// "theirs" swap meaning under a rebase</b> — git replays your commits onto theirs, so
    /// during a rebase "ours" is the upstream. The caller passes what the user chose and this
    /// passes it straight through; deciding which word to show belongs where the operation is
    /// known.
    /// </param>
    /// <remarks>
    /// Staged immediately afterwards, because <c>checkout --ours</c> writes the file and leaves
    /// it unmerged. A file that looks resolved on disk and is still conflicted in the index is
    /// the state that makes somebody think the button did nothing.
    /// </remarks>
    Task<GitResult> ResolveWithSideAsync(string path, bool ours, CancellationToken ct);

    /// <summary>
    /// Merge the upstream into this branch, producing conflicts rather than refusing.
    /// </summary>
    /// <remarks>
    /// The exit from a diverged branch that <c>pull --ff-only</c> cannot take. Deliberately a
    /// separate method rather than a flag on pull: the fast-forward-only default exists because
    /// auto-merging native CODESYS XML would produce a file nobody reviewed, and a merge is a
    /// thing the user has to ask for by name.
    /// </remarks>
    Task<GitResult> MergeUpstreamAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    /// <summary>Replay this branch's commits on top of the upstream.</summary>
    /// <remarks>
    /// The usual answer for a feature branch, and it keeps the exported history linear — which
    /// matters more here than in most repositories, because the diff being read is generated
    /// text and a merge commit in the middle of it explains nothing.
    /// </remarks>
    Task<GitResult> RebaseOntoUpstreamAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    /// <summary>Merge a named branch into the current one, producing conflicts rather than refusing.</summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="MergeUpstreamAsync"/>, which can only ever bring in this
    /// branch's own upstream and is offered only out of a diverged state. This is the ordinary
    /// "bring that work in here" that every git client has and Versio did not.
    /// </para>
    /// <para>
    /// <b>No <c>--no-ff</c></b>, for the reason <see cref="MergeUpstreamAsync"/> already gives:
    /// where the merge can fast-forward there is nothing to be gained by manufacturing a merge
    /// commit, and on an exported tree a linear history is easier to read back.
    /// </para>
    /// </remarks>
    Task<GitResult> MergeBranchAsync(
        string branch, IProgress<GitProgress>? progress, CancellationToken ct);

    /// <summary>
    /// The paths that differ between this branch and another, from their merge base.
    /// </summary>
    /// <remarks>
    /// <b>Three dots, and that is the whole point.</b> Two would list this branch's own commits
    /// as well, so a merge preflight would refuse on work that is already here. Three asks the
    /// question actually being posed: what would this merge <i>bring</i>?
    /// </remarks>
    /// <returns>
    /// The paths, or <c>null</c> when git could not answer — a branch with no resolvable ref,
    /// most often a remote-only name. Deliberately not an empty list, which would be
    /// indistinguishable from "brings nothing" and read as "already merged".
    /// </returns>
    Task<IReadOnlyList<string>?> IncomingPathsAsync(string branch, CancellationToken ct);

    /// <summary>
    /// Undo a merge or rebase that is part-way through, back to where it started.
    /// </summary>
    /// <remarks>
    /// <b>Not optional.</b> Offering an operation that can stop half-done without offering the
    /// way back is how somebody ends up in a state they cannot describe, in a tool that was
    /// supposed to keep them out of the terminal.
    /// </remarks>
    Task<GitResult> AbortMergeOrRebaseAsync(CancellationToken ct);

    /// <summary>Whether a merge or rebase is part-way through right now.</summary>
    Task<MergeState> MergeStateAsync(CancellationToken ct);

    /// <summary>
    /// Every remote-tracking ref, as <c>origin/branch</c>.
    /// </summary>
    /// <remarks>
    /// The other half of the branch list. <see cref="BranchesAsync"/> reads <c>refs/heads</c>,
    /// which is local branches only — so without this a branch that exists solely on the remote
    /// is invisible to the application, and the dropdown looks like the whole repository while
    /// being half of it.
    /// </remarks>
    Task<IReadOnlyList<string>> RemoteBranchesAsync(CancellationToken ct);

    /// <summary>
    /// Branches whose every commit is already on <paramref name="into"/>.
    /// </summary>
    /// <remarks>
    /// The single most useful fact in a delete dialog: the difference between deleting a
    /// tidy-up and deleting somebody's afternoon.
    /// </remarks>
    Task<IReadOnlyList<string>> MergedBranchesAsync(string into, CancellationToken ct);

    /// <summary>What the remote says its default branch is, or empty.</summary>
    /// <remarks>
    /// <c>origin/HEAD</c>, which most clones have and some do not — it is written at clone time
    /// and a repository that gained its remote later may never have had one. Empty is an answer.
    /// </remarks>
    Task<string> RemoteHeadAsync(string remote, CancellationToken ct);

    /// <summary>
    /// Delete a local branch.
    /// </summary>
    /// <param name="force">
    /// <c>-D</c> rather than <c>-d</c>. <b>Always try without it first.</b> Git refuses to
    /// delete an unmerged branch, and that refusal is information the user should see rather
    /// than something to escalate past.
    /// </param>
    Task<GitResult> DeleteBranchAsync(string name, bool force, CancellationToken ct);

    /// <summary>
    /// Delete a branch from the remote.
    /// </summary>
    /// <remarks>
    /// Outward-facing and effectively irreversible for teammates, so callers confirm with the
    /// branches and the remote named. There is no gentle form of this: git has no
    /// <c>--dry-run</c> equivalent that would tell you whether anybody else had it checked out.
    /// </remarks>
    Task<GitResult> DeleteRemoteBranchAsync(string remote, string name, CancellationToken ct);

    /// <summary>Verify a remote is reachable without cloning it.</summary>
    Task<GitResult> VerifyRemoteAsync(string url, CancellationToken ct);

    /// <summary>
    /// Where this repository was cloned from, or empty when it has no such remote.
    /// </summary>
    /// <remarks>
    /// Read while the repository is open and remembered, because the moment it is <i>needed</i>
    /// — offering to clone a vanished repository back — there is no <c>.git</c> left to ask.
    /// A repository with no remote is an ordinary local repository, not a failure.
    /// </remarks>
    Task<string> RemoteUrlAsync(string remote, CancellationToken ct);

    /// <summary>
    /// List refs on the remote matching a pattern, transferring no objects.
    /// </summary>
    /// <param name="pattern">
    /// A ref glob, e.g. <c>refs/pull/*/head</c>. Passed to git as an argument, never
    /// interpolated into a shell.
    /// </param>
    /// <returns>Raw <c>sha\tref</c> lines, or empty when nothing matched or the call failed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="LsRemoteAsync"/> because that one is about branches</b> — it
    /// passes <c>--heads</c> and builds <c>refs/heads/&lt;name&gt;</c> itself, which is exactly
    /// right for the poller and cannot reach the hidden namespaces a forge publishes its pull
    /// requests under. Widening it would have made the poller's own call able to match a tag.
    /// </para>
    /// <para>
    /// <b>This is what makes "does a merge request already exist?" answerable with no API
    /// token.</b> Both major forges publish request heads as refs readable with the same
    /// credentials as a fetch. The alternatives are worse in ways that do not announce
    /// themselves: <c>.git/config</c> knows nothing about requests, and a REST call means
    /// storing a token — a security surface of its own, for a link.
    /// </para>
    /// <para>
    /// Empty on failure rather than throwing, like the rest of the read-only probes here: this
    /// runs on the poller's schedule and a network blip must degrade the button's label, not
    /// take a panel down.
    /// </para>
    /// </remarks>
    Task<string> LsRemoteRefsAsync(string remote, string pattern, CancellationToken ct);

    /// <summary>
    /// The absolute path of this repository's <c>.git</c> directory.
    /// </summary>
    /// <remarks>
    /// Asked of git rather than assembled from the working tree root, because <c>.git</c> is a
    /// <i>file</i> in a linked worktree and a submodule keeps its directory somewhere else
    /// entirely. Anything writing next to git's own state has to know where that state is.
    /// </remarks>
    Task<string> GitDirectoryAsync(CancellationToken ct);

    /// <summary>
    /// Fetch from the remote, dropping tracking refs for branches that are gone.
    /// </summary>
    /// <remarks>
    /// Pruning is part of every fetch rather than a separate call. Without it a branch the
    /// forge deleted after merging lingers as a stale tracking ref: it reappears in the
    /// dropdown looking alive, and porcelain counts against it so the panel reports "Up to
    /// date" with a branch that no longer exists (FINDINGS 2026-08-23). A separate
    /// <c>FetchPruneAsync</c> existed for the in-app deletion flow, which meant the ordinary
    /// fetch — the button, the poller's catch-up — was the one that kept the ghosts.
    /// </remarks>
    Task<GitResult> FetchAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    Task<GitResult> PullAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    Task<GitResult> PushAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Every remote this repository has, in git's order.
    /// </summary>
    /// <remarks>
    /// Asked so that "this repository has no remote" and "this branch has no upstream" can be
    /// told apart. They are indistinguishable from the branch alone and their remedies have
    /// nothing in common — one is "add a remote", the other is "press Publish" — so answering
    /// the wrong one sends somebody hunting for a setting that is already correct.
    /// </remarks>
    Task<IReadOnlyList<string>> RemotesAsync(CancellationToken ct);

    /// <summary>
    /// Push a branch that has no upstream, and set one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A different command, so a different button.</b> A plain <c>git push</c> on a branch
    /// with no upstream fails outright — git has nowhere to send it — so this is not a push
    /// with a flag, it is the operation that makes a branch exist on the remote. Folding it
    /// into <see cref="PushAsync"/> would give one button two meanings and one failure mode
    /// that only appears sometimes.
    /// </para>
    /// <para>
    /// It releases the direction locks on success exactly as a push does. The lock rule is
    /// about work becoming visible to other people, not about which flag was passed.
    /// </para>
    /// </remarks>
    Task<GitResult> PublishBranchAsync(
        string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Unified diff for one path. <paramref name="staged"/> selects the index.
    /// </summary>
    /// <param name="contextLines">
    /// Unchanged lines to show either side of each change. Raise it to
    /// <see cref="GitDiff.FullFileContext"/> to render the whole file with its changes still
    /// marked — which is what a reviewer wants when three lines of context is not enough to
    /// tell whether an edit to a POU is safe.
    /// </param>
    Task<string> DiffAsync(string path, bool staged, CancellationToken ct, int contextLines = 3);

    /// <summary>Renames git detected, old path → new path. Used to re-link a binding whose
    /// project was renamed rather than treating it as delete + add.</summary>
    Task<IReadOnlyDictionary<string, string>> DetectRenamesAsync(CancellationToken ct);

    /// <summary>
    /// Commit id at HEAD, or empty on an unborn branch.
    /// </summary>
    /// <remarks>
    /// Recorded as the baseline when a push releases the direction locks: the lock records
    /// what the world has seen, and "what the world has seen" is a commit, not a moment.
    /// </remarks>
    Task<string> HeadCommitAsync(CancellationToken ct);

    /// <summary>Newest commits first, subject and author only.</summary>
    /// <param name="count">How many to read. The panel shows a page, not the history.</param>
    /// <param name="skip">
    /// How many newer commits to step over first, so the panel can ask for the next page
    /// rather than re-reading a longer prefix of the same log each time.
    /// </param>
    Task<IReadOnlyList<CommitEntry>> RecentCommitsAsync(int count, int skip, CancellationToken ct);

    /// <summary>Newest commits in one revision range — <c>@{u}..HEAD</c> and its mirror.</summary>
    /// <remarks>
    /// The ranges come from <see cref="GitRevisionRange"/>; an unresolvable one — the
    /// upstream deleted on the remote since the status was read — answers empty rather than
    /// throwing, because the preview it feeds is a glance, not a transaction.
    /// </remarks>
    Task<IReadOnlyList<CommitEntry>> CommitsInRangeAsync(
        string range, int count, CancellationToken ct);

    /// <summary>Every file one revision range touches, with what happened to each.</summary>
    /// <remarks>
    /// For the incoming preview this is the safety-relevant half: files arriving under an
    /// export tree will be imported into a <c>.project</c>, and which objects are about to
    /// move is worth knowing <i>before</i> the pull rather than after.
    /// </remarks>
    Task<IReadOnlyList<CommitFile>> RangeFilesAsync(string range, CancellationToken ct);

    /// <summary>
    /// The commits on this branch that no remote has, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not "ahead of upstream", and the difference matters.</b> Asking
    /// <c>@{upstream}..HEAD</c> answers nothing at all on a branch with no upstream — which is
    /// exactly the branch whose work is most invisible to everybody else — and a branch cut
    /// from a pushed <c>main</c> would then have to be special-cased so that <c>main</c>'s own
    /// commits were not reported as unpushed. <c>HEAD --not --remotes</c> asks the question the
    /// reader actually has, "can anybody else see this yet", and answers it correctly in all
    /// three cases: with an upstream, without one, and with no remote configured at all.
    /// </para>
    /// <para>
    /// Ids only. The history panel already has the commits; what it lacks is which of them are
    /// still only here.
    /// </para>
    /// <para>
    /// Capped, because a repository with no remote returns its entire history and the cap is
    /// what keeps that from being unbounded. Beyond the cap a commit is reported as pushed,
    /// which is the safe direction to be wrong in: it understates how much is local rather
    /// than marking ancient history as unsent.
    /// </para>
    /// </remarks>
    Task<IReadOnlySet<string>> UnpushedCommitIdsAsync(CancellationToken ct);

    /// <summary>The full patch of one commit, for click-to-diff.</summary>
    /// <remarks>
    /// A merge commit yields nothing here, because a diff against several parents is not one
    /// patch. Saying so beats picking a parent on the user's behalf and showing them a diff
    /// that is true against one side of a merge and misleading about the merge itself.
    /// </remarks>
    Task<string> CommitPatchAsync(string commit, CancellationToken ct,
                                  int contextLines = 3);

    /// <summary>
    /// Which files one commit touched, and what it did to each.
    /// </summary>
    /// <remarks>
    /// Asked separately from the patch, and only when somebody expands that commit. The list
    /// is what the history panel shows; the patches behind it are the expensive part, and a
    /// commit that touches an exported project tree can carry hundreds of them.
    /// <para>
    /// A merge yields nothing, for the same reason <see cref="CommitPatchAsync"/> does — git
    /// suppresses the combined diff, and inventing one by picking a parent would describe a
    /// change that nobody made.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<CommitFile>> CommitFilesAsync(string commit, CancellationToken ct);

    /// <summary>
    /// The patch one commit applied to one file.
    /// </summary>
    /// <param name="contextLines">
    /// As <see cref="DiffAsync"/>: raise it to <see cref="GitDiff.FullFileContext"/> to read
    /// the whole file with the commit's changes still marked.
    /// </param>
    Task<string> CommitFileDiffAsync(string commit, string path, CancellationToken ct,
                                     int contextLines = 3);

    /// <summary>Everything currently stashed, newest first.</summary>
    Task<IReadOnlyList<StashEntry>> StashListAsync(CancellationToken ct);

    /// <summary>
    /// Stash the working tree.
    /// </summary>
    /// <param name="includeUntracked">
    /// Also stash files git has never seen. Off by default because it sweeps up build output
    /// and scratch files that were never meant to be kept, and they come back on the pop.
    /// </param>
    Task<GitResult> StashPushAsync(string? message, bool includeUntracked, CancellationToken ct);

    /// <summary>
    /// Restore a stash, optionally dropping it afterwards.
    /// </summary>
    /// <remarks>
    /// Apply and pop are one call with a flag rather than two methods, because they are the
    /// same operation and differ only in whether the entry survives it — which is exactly the
    /// decision the caller is making.
    /// </remarks>
    Task<GitResult> StashApplyAsync(string stashRef, bool drop, CancellationToken ct);

    /// <summary>
    /// Delete a stash without restoring it.
    /// </summary>
    /// <remarks>
    /// <b>Destructive.</b> The work is unreachable afterwards by any ordinary means, so
    /// callers confirm first.
    /// </remarks>
    Task<GitResult> StashDropAsync(string stashRef, CancellationToken ct);

    /// <summary>
    /// Apply a patch to the index only, leaving the working tree untouched.
    /// </summary>
    /// <param name="reverse">
    /// Take the change back out of the index rather than putting it in — which is what
    /// unstaging one hunk is.
    /// </param>
    /// <remarks>
    /// The patch arrives on standard input rather than through a temporary file. A temp file
    /// is one more thing to fail to clean up, and one more place a fragment of somebody's
    /// source could be left readable.
    /// </remarks>
    Task<GitResult> ApplyToIndexAsync(string patch, bool reverse, CancellationToken ct);

    /// <summary>
    /// Whether <c>git lfs</c> is installed and usable in this repository.
    /// </summary>
    /// <remarks>
    /// Asked before writing an LFS rule into <c>.gitattributes</c>, never assumed. A
    /// <c>filter=lfs</c> rule in a repository without LFS fails every later checkout and
    /// commit, and the teammate who hits it did not choose LFS and cannot see where the rule
    /// came from (plan R10).
    /// </remarks>
    Task<bool> IsLfsAvailableAsync(CancellationToken ct);
}
