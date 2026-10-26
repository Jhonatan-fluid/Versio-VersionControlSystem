using Versio.Core.Git;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Git;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Drives the real <c>git</c> executable against a throwaway repository.
/// </summary>
/// <remarks>
/// Verifies the porcelain parser against output git actually produces, rather than against
/// fixtures written from memory. Fixtures drift; git does not.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GitCliClientTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _repo = CreateTempRepo();

    private static string CreateTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Versio", "gitit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try
        {
            // git marks objects read-only, so a plain delete fails.
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private GitCliClient Client => new(_repo, logger: new TestLogger(output));

    private async Task InitAsync()
    {
        await RunRawAsync("init", "-b", "main");
        await RunRawAsync("config", "user.email", "test@versio.local");
        await RunRawAsync("config", "user.name", "Versio Test");
    }

    private async Task RunRawAsync(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    /// <summary>Raw git, with what it printed and how it exited — for asking git's own opinion.</summary>
    private static async Task<(int ExitCode, string StdOut)> RunRawOutputAsync(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout);
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [SkippableFact]
    public async Task A_mirror_path_with_a_space_gets_an_attribute_line_git_reads_as_one_pattern()
    {
        // git splits an attribute line on whitespace, so the unquoted
        // `src/My App/export/** text eol=lf` the planner used to write was the pattern
        // `src/My` with three attributes — the mirror got no LF rule, and git said so when
        // asked. The 2026-10-24 review's item 26. Asked of git rather than of the planner's
        // own reader, because what git makes of the line is the only thing that matters.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        var plan = GitAttributes.Plan(string.Empty, ["*.project"], lfsAvailable: false,
                                      mirrors: ["src/My App/export"]);
        Write(".gitattributes", plan.Content);

        var (exit, answer) = await RunRawOutputAsync(_repo, "check-attr", "eol", "--", "src/My App/export/a.st");

        Assert.Equal(0, exit);
        Assert.Equal("src/My App/export/a.st: eol: lf", answer.Trim());
    }

    [SkippableFact]
    public async Task The_exclude_rule_for_a_linked_worktree_lands_where_git_reads_it()
    {
        // git reads info/exclude from the common directory only. The shell handed
        // LocalExclude the per-worktree directory (.git/worktrees/<name>, what
        // --absolute-git-dir answers there), wrote the rule under it, and git never read it:
        // .versio/local/ stayed untracked in every linked worktree and
        // `stash push --include-untracked` swept the auto-stash record into the stash it
        // described. The 2026-10-24 review's item 25. Asked of git with check-ignore, because
        // where git looks is the only thing that matters.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.txt", "a");
        await Client.StageAsync(["a.txt"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);
        await RunRawAsync("worktree", "add", "wt");

        var worktree = Path.Combine(_repo, "wt");
        var worktreeClient = new GitCliClient(worktree, logger: new TestLogger(output));

        var perWorktree = await worktreeClient.GitDirectoryAsync(CancellationToken.None);
        var common = await worktreeClient.CommonGitDirectoryAsync(CancellationToken.None);

        // The per-worktree answer is still the per-worktree one: the merge-state reader
        // depends on it and must not follow the exclude to the common directory.
        Assert.EndsWith(Path.Combine("worktrees", "wt"), perWorktree.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetFullPath(Path.Combine(_repo, ".git")),
                     Path.GetFullPath(common));

        await LocalExclude.EnsureAsync(common, new TestLogger(output), CancellationToken.None);

        var (exit, _) = await RunRawOutputAsync(worktree, "check-ignore", "-q", ".versio/local/x");

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(perWorktree, "info", "exclude")),
                     "the rule was written under the per-worktree directory, where git does not read it");
    }

    [SkippableFact]
    public async Task Head_is_empty_on_an_unborn_branch_and_a_sha_after_a_commit()
    {
        // A repository with no commits is an ordinary first-day state, and the caller - a
        // push releasing the direction locks - has nothing sensible to do with a throw.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Assert.Equal(string.Empty, await Client.HeadCommitAsync(CancellationToken.None));

        Write("plc/A.st", "PROGRAM A END_PROGRAM");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        var head = await Client.HeadCommitAsync(CancellationToken.None);
        output.WriteLine($"head: {head}");

        Assert.Equal(40, head.Length);
        Assert.DoesNotContain(head, c => !Uri.IsHexDigit(c));
    }

    // --- diff ---------------------------------------------------------------------

    [SkippableFact]
    public async Task A_brand_new_file_diffs_as_entirely_added()
    {
        // It used to show nothing at all. A new file has not got *no* diff - it is *all*
        // diff, and every line of it deserves reading before it is committed.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("plc/New.st", "PROGRAM New\nVAR\nEND_VAR\n");

        var diff = await Client.DiffAsync("plc/New.st", staged: false, CancellationToken.None);

        output.WriteLine(diff);
        Assert.Contains("new file", diff, StringComparison.OrdinalIgnoreCase);

        // Every content line is an addition, which is what paints the view green.
        var body = diff.Split('\n')
            .SkipWhile(l => !l.StartsWith("@@", StringComparison.Ordinal))
            .Skip(1)
            .Where(l => l.Trim().Length > 0)
            .ToList();

        Assert.NotEmpty(body);
        Assert.All(body, l => Assert.StartsWith("+", l, StringComparison.Ordinal));
        Assert.Contains(body, l => l.Contains("PROGRAM New", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_unmodified_tracked_file_still_shows_nothing()
    {
        // The other direction, and the more alarming failure: falling back to --no-index
        // whenever the primary diff came back empty would render an untouched file as though
        // every line had just been added.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("plc/Existing.st", "PROGRAM Existing\nEND_PROGRAM\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        var diff = await Client.DiffAsync("plc/Existing.st", staged: false, CancellationToken.None);

        Assert.True(string.IsNullOrWhiteSpace(diff), "expected no diff, got: " + diff);
    }

    [SkippableFact]
    public async Task Full_file_context_shows_unchanged_lines_that_the_default_hides()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        // Long enough that the default three lines of context cannot reach either end.
        var original = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}")) + "\n";
        Write("plc/Long.st", original);
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("plc/Long.st", original.Replace("line 20\n", "line 20 edited\n"));

        var narrow = await Client.DiffAsync("plc/Long.st", staged: false, CancellationToken.None);
        var full = await Client.DiffAsync(
            "plc/Long.st", staged: false, CancellationToken.None, GitDiff.FullFileContext);

        // The edit shows in both; only the full view reaches the far ends of the file.
        Assert.Contains("line 20 edited", narrow, StringComparison.Ordinal);
        Assert.Contains("line 20 edited", full, StringComparison.Ordinal);

        Assert.DoesNotContain("+line 1\n", narrow, StringComparison.Ordinal);
        Assert.DoesNotContain(" line 1\n", narrow, StringComparison.Ordinal);
        Assert.Contains(" line 1\n", full, StringComparison.Ordinal);
        Assert.Contains("line 40", full, StringComparison.Ordinal);

        output.WriteLine($"default {narrow.Length} chars, full {full.Length} chars");
        Assert.True(full.Length > narrow.Length);
    }

    [SkippableFact]
    public async Task A_non_repository_is_detected()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");

        Assert.False(await Client.IsRepositoryAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_status_git_could_not_produce_says_so_rather_than_reading_as_clean()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");

        // Not a repository, so `git status` exits 128 with nothing on stdout. The parse of
        // that is an empty status, which IsClean calls clean; the adoption check treated it as
        // "nothing changed" (review, 2026-10-17). The failure has to travel with it.
        var status = await Client.StatusAsync(CancellationToken.None);

        Assert.False(status.WasRead);
        Assert.False(string.IsNullOrWhiteSpace(status.Failure));
        output.WriteLine(status.Failure);
    }

    [SkippableFact]
    public async Task A_status_git_did_produce_is_marked_as_read()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Assert.True((await Client.StatusAsync(CancellationToken.None)).WasRead);
    }

    [SkippableFact]
    public async Task A_fresh_repo_reports_an_unborn_branch()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        var status = await Client.StatusAsync(CancellationToken.None);

        Assert.True(await Client.IsRepositoryAsync(CancellationToken.None));
        Assert.Equal("main", status.Branch.Name);
        Assert.True(status.Branch.IsUnborn);
        Assert.True(status.IsClean);
    }

    [SkippableFact]
    public async Task Untracked_staged_and_modified_states_round_trip_through_the_parser()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("plc/Main.st", "PROGRAM Main\nVAR\nEND_VAR\n");
        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal(ChangeKind.Untracked,
            Assert.Single(status.Changes, c => c.Path == "plc/Main.st").Unstaged);

        await Client.StageAsync(["plc/Main.st"], CancellationToken.None);
        status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal(ChangeKind.Added,
            Assert.Single(status.Changes, c => c.Path == "plc/Main.st").Staged);

        var commit = await Client.CommitAsync("add Main", CancellationToken.None);
        Assert.True(commit.Success, commit.FailureReason);
        Assert.True((await Client.StatusAsync(CancellationToken.None)).IsClean);

        Write("plc/Main.st", "PROGRAM Main\nVAR\n  x : INT;\nEND_VAR\n");
        status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal(ChangeKind.Modified,
            Assert.Single(status.Changes, c => c.Path == "plc/Main.st").Unstaged);
    }

    [SkippableFact]
    public async Task Paths_with_spaces_and_non_ascii_survive_the_z_format()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        // Exactly the shape a real export produces: "Device/Plc Logic/Application/...".
        const string path = "Device/Plc Logic/Application/Kühlschrank Regelung.st";
        Write(path, "PROGRAM X\n");

        var status = await Client.StatusAsync(CancellationToken.None);

        Assert.Contains(status.Changes, c => c.Path == path);
    }

    [SkippableFact]
    public async Task A_rename_is_reported_with_its_original_path()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("plc/Old.st", new string('x', 400));
        await Client.StageAsync(["plc/Old.st"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        File.Move(Path.Combine(_repo, "plc/Old.st"), Path.Combine(_repo, "plc/New.st"));
        await Client.StageAsync(["plc"], CancellationToken.None);

        var status = await Client.StatusAsync(CancellationToken.None);
        var renamed = Assert.Single(status.Changes, c => c.Staged == ChangeKind.Renamed);

        Assert.Equal("plc/New.st", renamed.Path);
        Assert.Equal("plc/Old.st", renamed.OriginalPath);

        // This is what lets a renamed project keep its binding id, baseline and lock.
        var renames = await Client.DetectRenamesAsync(CancellationToken.None);
        Assert.Equal("plc/New.st", renames["plc/Old.st"]);
    }

    [SkippableFact]
    public async Task A_rename_survives_a_modified_file_whose_name_starts_with_R()
    {
        // The client scanned the -z fields itself and took any field starting with R as a
        // rename status. A modified README.txt listed before the rename put "README.txt" in
        // the status position, so the scan recorded R100 → plc/Old.st as the rename and lost
        // the real one: the binding reported broken and the new path untracked. The
        // 2026-10-24 review's item 4.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("README.txt", "about");
        Write("plc/Old.st", new string('x', 400));
        await Client.StageAsync(["README.txt", "plc/Old.st"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        Write("README.txt", "about, edited");
        File.Move(Path.Combine(_repo, "plc/Old.st"), Path.Combine(_repo, "plc/New.st"));
        await Client.StageAsync(["README.txt", "plc"], CancellationToken.None);

        var renames = await Client.DetectRenamesAsync(CancellationToken.None);

        var rename = Assert.Single(renames);
        Assert.Equal("plc/Old.st", rename.Key);
        Assert.Equal("plc/New.st", rename.Value);
    }

    [SkippableFact]
    public async Task Incoming_paths_with_non_ascii_names_arrive_verbatim_not_quoted()
    {
        // The merge guard matches what a branch would bring against the tracked projects by
        // string: an exact path, or a prefix under the mirror. git's default core.quotePath
        // writes a non-ASCII name as "Pr\303\274fung.project" — quotes included — on any
        // listing without -z, and that string matches no project and no mirror, so the guard
        // reported "touches nothing" and the import protection was skipped. The 2026-10-24
        // review's item 15.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.txt", "a");
        await Client.StageAsync(["a.txt"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        var created = await Client.CreateBranchAsync("topic", checkout: true, CancellationToken.None);
        Assert.True(created.Success, created.FailureReason);
        Write("plc/Prüfung.project", "binary-ish");
        Write("plc/Prüfung/Kühl.st", "PROGRAM Kühl\nEND_PROGRAM\n");
        await Client.StageAsync(["plc"], CancellationToken.None);
        await Client.CommitAsync("umlauts", CancellationToken.None);
        Assert.True((await Client.CheckoutAsync("main", CancellationToken.None)).Success);

        var incoming = await Client.IncomingPathsAsync("topic", CancellationToken.None);

        Assert.NotNull(incoming);
        Assert.Equal(["plc/Prüfung.project", "plc/Prüfung/Kühl.st"], incoming.Order());

        var plan = MergePreflight.For(
            "topic", incoming,
            [new TrackedRoot { BindingId = "b1", Name = "Prüfung", ProjectPath = "plc/Prüfung.project", MirrorPath = "plc/Prüfung" }]);
        Assert.Equal(MergeVerdict.TouchesTrackedProject, plan.Verdict);
    }

    [SkippableFact]
    public async Task Incoming_paths_keep_their_leading_spaces()
    {
        // The -z listing above was split with TrimEntries to drop the runner's trailing
        // newline, and TrimEntries trims every field: " plc/Machine.project" arrived as
        // "plc/Machine.project", matched no tracked root, and the merge guard let the merge
        // through for a project it was tracking (the second-round review's item 2). The
        // newline is removed by length now; a name with a space at either end is the name.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.txt", "a");
        await Client.StageAsync(["a.txt"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        var created = await Client.CreateBranchAsync("topic", checkout: true, CancellationToken.None);
        Assert.True(created.Success, created.FailureReason);
        Write(" plc/Machine.project", "binary-ish");
        Write(" plc/Machine/ Main.st", "PROGRAM Main\nEND_PROGRAM\n");
        await Client.StageAsync([" plc"], CancellationToken.None);
        await Client.CommitAsync("leading spaces", CancellationToken.None);
        Assert.True((await Client.CheckoutAsync("main", CancellationToken.None)).Success);

        var incoming = await Client.IncomingPathsAsync("topic", CancellationToken.None);

        Assert.NotNull(incoming);
        Assert.Equal([" plc/Machine.project", " plc/Machine/ Main.st"], incoming.Order());

        var plan = MergePreflight.For(
            "topic", incoming,
            [new TrackedRoot { BindingId = "b1", Name = "Machine", ProjectPath = " plc/Machine.project", MirrorPath = " plc/Machine" }]);
        Assert.Equal(MergeVerdict.TouchesTrackedProject, plan.Verdict);
    }

    [SkippableFact]
    public async Task A_file_the_working_tree_has_lost_is_still_readable_at_HEAD()
    {
        // The only source of a departed file's content. The import planner needs it to pair a
        // file that was renamed AND edited with the one it became — no hash can, because the
        // content changed, and the manifest records a hash rather than the bytes.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        // A trailing newline on purpose: the exporter writes one and hashes what it writes, so
        // a reader that trimmed would make every comparison fail for a reason that has nothing
        // to do with the file.
        Write("plc/Old.st", "PROGRAM Old\nx := 1;\n");
        await Client.StageAsync(["plc/Old.st"], CancellationToken.None);
        await Client.CommitAsync("add", CancellationToken.None);

        File.Delete(Path.Combine(_repo, "plc", "Old.st"));

        var atHead = await Client.ShowAtHeadAsync("plc/Old.st", CancellationToken.None);
        Assert.Equal("PROGRAM Old\nx := 1;\n", atHead.Replace("\r\n", "\n"));

        // A path HEAD never had is empty rather than an error: for the caller, "never
        // committed" and "no such file" are the same ordinary answer — there is no old copy,
        // so a rename falls back to a delete and a create.
        Assert.Equal(
            string.Empty, await Client.ShowAtHeadAsync("plc/Never.st", CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_index_is_weighed_not_the_working_copy_that_replaced_it()
    {
        // The large-file preflight measured FileInfo, and the commit records the index: a
        // blob staged at a megabyte and then overwritten on disk with five bytes weighed five
        // bytes to the preflight and a megabyte to git (the 2026-10-24 review's item 4).
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        const string path = "Device/Plc Logic/Kühlschrank Regelung.project";
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[1024 * 1024]);
        await Client.StageAsync([path], CancellationToken.None);

        File.WriteAllText(full, "tiny!");
        Write("plc/Never.st", "PROGRAM Never\n");

        var sizes = await Client.IndexBlobSizesAsync(
            [path, "plc/Never.st", "Device"], CancellationToken.None);

        Assert.NotNull(sizes);
        Assert.Equal(1024 * 1024, sizes[path]);

        // Never staged, and a directory: neither has a blob in the index, and neither is a
        // failure — absent, so the caller falls back to whatever it measures otherwise.
        Assert.DoesNotContain("plc/Never.st", sizes.Keys);
        Assert.DoesNotContain("Device", sizes.Keys);
    }

    [SkippableFact]
    public async Task Branches_are_listed_with_the_current_one_marked()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("a.txt", "a");
        await Client.StageAsync(["a.txt"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        var created = await Client.CreateBranchAsync("feature/x", checkout: true, CancellationToken.None);
        Assert.True(created.Success, created.FailureReason);

        var branches = await Client.BranchesAsync(CancellationToken.None);

        Assert.Equal(2, branches.Count);
        Assert.Equal("feature/x", Assert.Single(branches, b => b.IsCurrent).Name);
    }

    [SkippableFact]
    public async Task Discard_restores_a_modified_file()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("a.txt", "original");
        await Client.StageAsync(["a.txt"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        Write("a.txt", "changed");
        var discard = await Client.DiscardAsync(["a.txt"], CancellationToken.None);

        Assert.True(discard.Success, discard.FailureReason);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_repo, "a.txt")));
        Assert.True((await Client.StatusAsync(CancellationToken.None)).IsClean);
    }

    [SkippableFact]
    public async Task A_selected_path_is_a_name_and_never_a_pattern()
    {
        // Every path git takes is a pathspec, and a pathspec is a glob unless told otherwise:
        // "part[1].st" matches part1.st as well. Discarding the one file the owner ticked then
        // threw away another file's edits, and nothing in git's output said so (the
        // 2026-10-24 review's item 1). Staging had the same reach; resolving a conflict too.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("part[1].st", "bracket");
        Write("part1.st", "plain");
        await Client.StageAsync(["part[1].st", "part1.st"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        Write("part[1].st", "bracket edited");
        Write("part1.st", "plain edited");

        // Stage: only the bracketed file goes in.
        var staged = await Client.StageAsync(["part[1].st"], CancellationToken.None);
        Assert.True(staged.Success, staged.FailureReason);
        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal(ChangeKind.Modified, Assert.Single(status.Changes, c => c.Path == "part[1].st").Staged);
        Assert.Equal(ChangeKind.Unmodified, Assert.Single(status.Changes, c => c.Path == "part1.st").Staged);

        // Discard: only the bracketed file's edit is lost.
        await Client.UnstageAsync(["part[1].st"], CancellationToken.None);
        var discard = await Client.DiscardAsync(["part[1].st"], CancellationToken.None);
        Assert.True(discard.Success, discard.FailureReason);

        Assert.Equal("bracket", File.ReadAllText(Path.Combine(_repo, "part[1].st")));
        Assert.Equal("plain edited", File.ReadAllText(Path.Combine(_repo, "part1.st")));
    }

    [SkippableFact]
    public async Task Unstaging_works_before_the_first_commit_and_leaves_the_other_staged_file_alone()
    {
        // `git restore --staged` restores the index entry from HEAD, and a repository with no
        // commit yet has no HEAD to restore from: "fatal: could not resolve 'HEAD'". So on the
        // first day of a repository, ticking one of two staged files and pressing Commit
        // failed at the unstage and committed nothing (the 2026-10-25 review's P2). Before
        // the first commit the index is a diff against the empty tree, which is what the
        // client restores from instead.
        //
        // a.txt is edited AFTER it was staged. That is the state `git rm --cached` refuses
        // without -f ("staged content different from both the file and the HEAD"), and the
        // reason it was not the fix: a flag spelled "force" on an unstage would be the wrong
        // thing to find in the log of a tool whose whole point is not losing work.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("a.txt", "a");
        Write("b.txt", "b");
        await Client.StageAsync(["a.txt", "b.txt"], CancellationToken.None);
        Write("a.txt", "a edited after staging");

        var result = await Client.UnstageAsync(["a.txt"], CancellationToken.None);
        Assert.True(result.Success, result.FailureReason);

        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.True(status.Branch.IsUnborn);
        Assert.Equal(ChangeKind.Untracked, Assert.Single(status.Changes, c => c.Path == "a.txt").Unstaged);
        Assert.Equal(ChangeKind.Added, Assert.Single(status.Changes, c => c.Path == "b.txt").Staged);

        // Unstaging is about the index only: the working file keeps the edit.
        Assert.Equal("a edited after staging", File.ReadAllText(Path.Combine(_repo, "a.txt")));
    }

    [SkippableFact]
    public async Task Unstaging_several_paths_before_the_first_commit_takes_them_all_out_in_one_call()
    {
        // The shell hands over a list — a staged rename is unstaged by both of its paths —
        // and the unborn-branch form has to take a list the way the ordinary one does.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("a.txt", "a");
        Write("b.txt", "b");
        Write("c.txt", "c");
        await Client.StageAsync(["a.txt", "b.txt", "c.txt"], CancellationToken.None);

        var result = await Client.UnstageAsync(["a.txt", "c.txt"], CancellationToken.None);
        Assert.True(result.Success, result.FailureReason);

        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal(ChangeKind.Untracked, Assert.Single(status.Changes, c => c.Path == "a.txt").Unstaged);
        Assert.Equal(ChangeKind.Added, Assert.Single(status.Changes, c => c.Path == "b.txt").Staged);
        Assert.Equal(ChangeKind.Untracked, Assert.Single(status.Changes, c => c.Path == "c.txt").Unstaged);
    }

    [SkippableFact]
    public async Task An_empty_commit_message_is_refused_before_git_is_invoked()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        var result = await Client.CommitAsync("   ", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("message", result.FailureReason);
    }

    [SkippableFact]
    public async Task An_unreachable_remote_fails_without_hanging_on_a_prompt()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        // GIT_TERMINAL_PROMPT=0 is what stops this blocking forever on a credential prompt
        // under a redirected console, with nothing on screen to explain the hang.
        var result = await Client.VerifyRemoteAsync(
            "https://invalid.versio.test/nope.git", CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        output.WriteLine($"failure: {result.FailureReason}");
    }

    [SkippableFact]
    public async Task A_repository_can_be_cloned_and_the_clone_carries_the_history()
    {
        // No network needed: a local path is a perfectly good remote, so this exercises the
        // real clone path — process, arguments, working directory, progress — without making
        // the suite depend on somebody's connection or on a host staying up.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("plc/Main.st", "PROGRAM Main\nEND_PROGRAM\n");
        await Client.StageAsync(["plc/Main.st"], CancellationToken.None);
        await Client.CommitAsync("first", CancellationToken.None);

        var target = Path.Combine(_repo, "..", $"clone-{Guid.NewGuid():N}"[..14]);
        var seen = new List<GitProgress>();

        var result = await GitCliClient.CloneAsync(
            _repo, target, "git", new Progress<GitProgress>(p => seen.Add(p)),
            new TestLogger(output), CancellationToken.None);

        try
        {
            Assert.True(result.Success, result.FailureReason);

            // The folder is created BY git, from the last segment of the path — cloning into
            // a folder that had to exist first would silently accept a non-empty one.
            Assert.True(Directory.Exists(target), "the clone directory was not created");
            Assert.True(File.Exists(Path.Combine(target, "plc", "Main.st")),
                        "the working tree did not arrive");

            // And it is a real repository, not just files: the history came too.
            var clone = new GitCliClient(target, logger: new TestLogger(output));
            Assert.True(await clone.IsRepositoryAsync(CancellationToken.None));
            Assert.NotEmpty(await clone.HeadCommitAsync(CancellationToken.None));
        }
        finally
        {
            SafeDelete(target);
        }
    }

    [SkippableFact]
    public async Task Cloning_into_a_path_with_no_parent_is_refused_before_git_runs()
    {
        // A drive root has no parent, and handing it to git would clone the repository into
        // the root of the disk. Refused with a sentence rather than attempted — and refused
        // WITHOUT running git, which is why this needs no repository of its own.
        Skip.IfNot(GitAvailable, "git not on PATH");

        var result = await GitCliClient.CloneAsync(
            _repo, "C:\\", "git", null, new TestLogger(output), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cloned into", result.FailureReason);
    }

    [SkippableFact]
    public async Task Cloning_a_url_that_is_not_a_repository_fails_with_gits_own_words()
    {
        // The failure a person actually hits: a typo in the URL. It must arrive as a sentence
        // rather than as an exit code, because it is shown in a dialog.
        Skip.IfNot(GitAvailable, "git not on PATH");

        var target = Path.Combine(_repo, "..", $"nope-{Guid.NewGuid():N}"[..13]);

        var result = await GitCliClient.CloneAsync(
            Path.Combine(_repo, "not-a-repository"), target, "git", null,
            new TestLogger(output), CancellationToken.None);

        try
        {
            Assert.False(result.Success);
            Assert.NotNull(result.FailureReason);
            output.WriteLine($"failure: {result.FailureReason}");
        }
        finally
        {
            SafeDelete(target);
        }
    }

    /// <summary>Remove a throwaway clone, read-only git objects and all.</summary>
    private static void SafeDelete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var f in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [SkippableFact]
    public async Task A_diff_is_produced_for_a_modified_file()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();
        Write("plc/Main.st", "PROGRAM Main\nVAR\nEND_VAR\n");
        await Client.StageAsync(["plc/Main.st"], CancellationToken.None);
        await Client.CommitAsync("initial", CancellationToken.None);

        Write("plc/Main.st", "PROGRAM Main\nVAR\n  nSpeed : INT;\nEND_VAR\n");
        var diff = await Client.DiffAsync("plc/Main.st", staged: false, CancellationToken.None);

        Assert.Contains("+  nSpeed : INT;", diff);
    }

    [SkippableFact]
    public async Task Staging_hundreds_of_long_paths_does_not_overflow_CreateProcess()
    {
        // Win32 206. Stage all & commit on this repository handed git 336 TIA
        // fixture paths as argv (2026-08-21) and the process never started.
        // Pathspec-from-file is the seam; this many long names is the subject.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        const string folder =
            "fixtures/projects/CleaningStation_V20_V21/CleaningStation_V20_V21/"
            + "PLC data types/Connector - data types";
        const int count = 400;
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var relative = $"{folder}/UDT_MTP_LongName_{i:D4}.udt";
            paths.Add(relative);
            Write(relative, $"TYPE UDT_{i}\nEND_TYPE\n");
        }

        // The argv this used to build would not fit. If a future change puts
        // the paths back on the command line, this is the number that breaks
        // Windows before git can refuse them.
        var argvChars = "git add -- ".Length + paths.Sum(p => p.Length + 3);
        Assert.True(argvChars > 32_767, $"fixture too small to overflow CreateProcess ({argvChars})");

        var result = await Client.StageAsync(paths, CancellationToken.None);
        Assert.True(result.Success, result.FailureReason);

        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal(count, status.Changes.Count(c => c.Staged == ChangeKind.Added));
    }

    [SkippableFact]
    public async Task A_path_that_looks_like_a_flag_still_stages()
    {
        // The argv form used `--` for this. Pathspec-from-file must keep it.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("--odd.st", "PROGRAM Odd\nEND_PROGRAM\n");
        var result = await Client.StageAsync(["--odd.st"], CancellationToken.None);
        Assert.True(result.Success, result.FailureReason);

        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal(ChangeKind.Added,
            Assert.Single(status.Changes, c => c.Path == "--odd.st").Staged);
    }

    /// <summary>
    /// A merge where one branch edited the file and the other deleted it.
    /// </summary>
    /// <remarks>
    /// The direction matters, which is why it is a parameter: git keeps the surviving version
    /// at stage 2 when this branch kept the file and at stage 3 when the incoming branch did,
    /// and the side that deleted has no stage at all. The path has a space in it because real
    /// mirror paths do, and a space is the character a listing parser is likeliest to split on.
    /// </remarks>
    private async Task MakeAModifyDeleteConflictAsync(string path, bool oursDeletes)
    {
        await InitAsync();
        Write(path, "shared\noriginal\n");
        await RunRawAsync("add", "--", path);
        await RunRawAsync("commit", "-m", "base");

        await RunRawAsync("checkout", "-b", "incoming");
        if (oursDeletes)
        {
            Write(path, "shared\ntheir edit\n");
            await RunRawAsync("commit", "-am", "incoming edits");
        }
        else
        {
            await RunRawAsync("rm", "--", path);
            await RunRawAsync("commit", "-m", "incoming deletes");
        }

        await RunRawAsync("checkout", "main");
        if (oursDeletes)
        {
            await RunRawAsync("rm", "--", path);
            await RunRawAsync("commit", "-m", "ours deletes");
        }
        else
        {
            Write(path, "shared\nour edit\n");
            await RunRawAsync("commit", "-am", "ours edits");
        }

        // Expected to fail. That failure is the state under test.
        await RunRawAsync("merge", "incoming");
        var (_, unmerged) = await RunRawOutputAsync(_repo, "ls-files", "-u", "--", path);
        Assert.False(string.IsNullOrWhiteSpace(unmerged), "the fixture did not produce a conflict");
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolving_a_modify_delete_conflict_with_the_side_that_deleted_removes_the_file(bool oursDeletes)
    {
        // The deleting side has no version of the file, so `checkout --ours` (or --theirs) has
        // nothing to write and git says so and exits 1. The first version ran exactly that and
        // reported the failure — the conflict stayed, the file stayed, and the button looked
        // broken (the 2026-10-25 review, P2). Taking the deleting side means the file leaves the
        // working tree and its removal is what gets staged.
        Skip.IfNot(GitAvailable, "git not on PATH");
        const string path = "plc/Motor Control.st";
        await MakeAModifyDeleteConflictAsync(path, oursDeletes);

        var result = await Client.ResolveWithSideAsync(path, ours: oursDeletes, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        var (_, unmerged) = await RunRawOutputAsync(_repo, "ls-files", "-u", "--", path);
        Assert.Equal(string.Empty, unmerged.Trim());
        var (_, indexed) = await RunRawOutputAsync(_repo, "ls-files", "--", path);
        Assert.Equal(string.Empty, indexed.Trim());
        Assert.False(File.Exists(Path.Combine(_repo, path)), "the deleted side's file is still in the tree");
        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.DoesNotContain(status.Changes, c => c.Path == path && c.IsConflicted);

        if (!oursDeletes)
        {
            // Against HEAD, which still has the file, the resolution reads as a staged delete.
            // When HEAD is the side that deleted there is nothing to show: the index already
            // matches it.
            Assert.Equal(ChangeKind.Deleted, Assert.Single(status.Changes, c => c.Path == path).Staged);
        }
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolving_a_modify_delete_conflict_with_the_side_that_kept_the_file_keeps_its_edit(bool oursDeletes)
    {
        // The other button in the same conflict. It worked before the fix, and the deleting-side
        // path must not have changed that: the stage lookup picks checkout + add here.
        Skip.IfNot(GitAvailable, "git not on PATH");
        const string path = "plc/Motor Control.st";
        await MakeAModifyDeleteConflictAsync(path, oursDeletes);

        var result = await Client.ResolveWithSideAsync(path, ours: !oursDeletes, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        var (_, unmerged) = await RunRawOutputAsync(_repo, "ls-files", "-u", "--", path);
        Assert.Equal(string.Empty, unmerged.Trim());
        // Line endings normalised: checkout rewrites them under core.autocrlf, and that is
        // git's business, not this test's.
        Assert.Equal(oursDeletes ? "shared\ntheir edit\n" : "shared\nour edit\n",
                     File.ReadAllText(Path.Combine(_repo, path)).Replace("\r\n", "\n"));
        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.DoesNotContain(status.Changes, c => c.Path == path && c.IsConflicted);
    }

    private static bool GitAvailable
    {
        get
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("git", "--version")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                p!.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
