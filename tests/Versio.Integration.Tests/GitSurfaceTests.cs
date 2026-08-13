using Versio.Core.Diff;
using Versio.Core.Git;
using Versio.Infrastructure.Git;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The stash, the history list and per-hunk staging, against the real <c>git</c>.
/// </summary>
/// <remarks>
/// <para>
/// The parsers are unit-tested against output written by hand, which proves they read the
/// shape they were designed for and nothing about whether git produces that shape. These tests
/// close that gap: they ask git for the formats the client actually sends and assert on what
/// comes back, so a format string that is subtly wrong fails here rather than as an empty list
/// in the panel.
/// </para>
/// <para>
/// Per-hunk staging is the one that could not be proven any other way. Whether
/// <see cref="HunkPatchBuilder"/> produces something <c>git apply --cached</c> accepts is a
/// question only git can answer, and a patch that is subtly malformed is rejected with git's
/// own wording about a file the user never wrote.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GitSurfaceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _repo = CreateTempRepo();

    private static string CreateTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Versio", "gitsurface",
                               Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try
        {
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

        // These tests compare file contents byte for byte after a checkout, and on Windows
        // git's autocrlf filter rewrites LF to CRLF on the way out. That is git working as
        // configured rather than anything under test, and leaving it on would make the
        // assertions about what a stash restored into a measurement of the filter.
        await RunRawAsync("config", "core.autocrlf", "false");
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

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_repo, relative));

    // --- history ------------------------------------------------------------------

    [SkippableFact]
    public async Task The_commit_format_round_trips_through_the_parser()
    {
        // The format string and the parser are two halves of one agreement, and only git
        // sitting between them can say whether they still agree.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "one\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first: the beginning");

        Write("a.st", "two\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "second\twith a tab and a | pipe");

        var commits = await Client.RecentCommitsAsync(10, 0, CancellationToken.None);

        foreach (var c in commits)
        {
            output.WriteLine($"{c.ShortId} {c.Age} {c.Author}: {c.Subject}");
        }

        Assert.Equal(2, commits.Count);

        // Newest first, which is git's order and the one the panel shows.
        Assert.Equal("second\twith a tab and a | pipe", commits[0].Subject);
        Assert.Equal("first: the beginning", commits[1].Subject);

        Assert.Equal(40, commits[0].Id.Length);
        Assert.StartsWith(commits[0].ShortId, commits[0].Id, StringComparison.Ordinal);
        Assert.Equal("Versio Test", commits[0].Author);
        Assert.NotNull(commits[0].When);
        Assert.NotEmpty(commits[0].Age);
    }

    [SkippableFact]
    public async Task An_unborn_branch_has_no_history_rather_than_an_error()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Assert.Empty(await Client.RecentCommitsAsync(10, 0, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_commits_patch_parses_as_a_diff()
    {
        // Click-to-diff feeds this straight into the same view the working tree uses, so it
        // has to arrive as a plain unified diff with no commit header in front of it.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "one\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", "one\ntwo\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "second");

        var head = await Client.HeadCommitAsync(CancellationToken.None);
        var patch = await Client.CommitPatchAsync(head, CancellationToken.None);

        output.WriteLine(patch);

        Assert.StartsWith("diff --git", patch, StringComparison.Ordinal);

        var document = UnifiedDiffParser.Parse(patch);
        Assert.Equal(1, document.Hunks);
        Assert.Equal(1, document.Added);
    }

    /// <summary>
    /// Paging reads a window further back rather than a longer prefix.
    /// </summary>
    /// <remarks>
    /// The history panel asks for one page at a time, and the whole point is that the second
    /// page costs the same as the first. A <c>--skip</c> that git ignored would show the same
    /// commits twice, which is exactly the kind of thing that looks like a UI bug.
    /// </remarks>
    [SkippableFact]
    public async Task A_later_page_of_history_starts_where_the_previous_one_ended()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        for (var i = 1; i <= 5; i++)
        {
            Write("a.st", $"revision {i}\n");
            await RunRawAsync("add", "-A");
            await RunRawAsync("commit", "-m", $"commit {i}");
        }

        var first = await Client.RecentCommitsAsync(2, 0, CancellationToken.None);
        var second = await Client.RecentCommitsAsync(2, 2, CancellationToken.None);
        var past = await Client.RecentCommitsAsync(2, 5, CancellationToken.None);

        Assert.Equal(["commit 5", "commit 4"], first.Select(c => c.Subject));
        Assert.Equal(["commit 3", "commit 2"], second.Select(c => c.Subject));

        // Running off the end is how the panel learns there is nothing more to load.
        Assert.Empty(past);
    }

    /// <summary>
    /// The file list behind one commit, including a rename and a delete.
    /// </summary>
    /// <remarks>
    /// The two-path statuses are the ones worth proving against real git: the parser's
    /// handling of them is what keeps every row after a rename from being read as garbage,
    /// and whether git emits <c>R100</c> at all depends on rename detection being on by
    /// default — which is a property of git, not of the parser.
    /// </remarks>
    [SkippableFact]
    public async Task A_commits_file_list_names_every_file_and_what_happened_to_it()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("keep.st", "one\n");
        Write("move me.st", "the content that is about to move\n");
        Write("doomed.st", "temporary\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("keep.st", "two\n");
        Write("Kühlschrank Regelung.st", "the content that is about to move\n");
        File.Delete(Path.Combine(_repo, "move me.st"));
        File.Delete(Path.Combine(_repo, "doomed.st"));
        Write("added.st", "brand new\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "second");

        var head = (await Client.RecentCommitsAsync(1, 0, CancellationToken.None))[0];
        var files = await Client.CommitFilesAsync(head.Id, CancellationToken.None);

        foreach (var f in files)
        {
            output.WriteLine($"{f.Kind} {f.OriginalPath} -> {f.Path}");
        }

        var byPath = files.ToDictionary(f => f.Path);

        Assert.Equal(ChangeKind.Modified, byPath["keep.st"].Kind);
        Assert.Equal(ChangeKind.Added, byPath["added.st"].Kind);
        Assert.Equal(ChangeKind.Deleted, byPath["doomed.st"].Kind);

        // The space and the umlaut are the reason the client asks for -z. Quoted output
        // would arrive here wrapped in quotes with the umlaut as octal escapes, and the
        // lookup below would simply not find it.
        var renamed = byPath["Kühlschrank Regelung.st"];
        Assert.Equal(ChangeKind.Renamed, renamed.Kind);
        Assert.Equal("move me.st", renamed.OriginalPath);
    }

    /// <summary>
    /// One file's patch out of a commit that touched several.
    /// </summary>
    /// <remarks>
    /// This feeds the same diff view the working tree uses, so it has to arrive as a plain
    /// unified diff naming that file and nothing else — a patch carrying the other files
    /// would render the wrong change under the row the user clicked.
    /// </remarks>
    [SkippableFact]
    public async Task One_files_patch_can_be_read_out_of_a_commit_on_its_own()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "a one\n");
        Write("b.st", "b one\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", "a two\n");
        Write("b.st", "b two\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "second");

        var head = (await Client.RecentCommitsAsync(1, 0, CancellationToken.None))[0];
        var patch = await Client.CommitFileDiffAsync(head.Id, "a.st", CancellationToken.None);

        output.WriteLine(patch);

        Assert.Contains("--- a/a.st", patch, StringComparison.Ordinal);
        Assert.Contains("-a one", patch, StringComparison.Ordinal);
        Assert.Contains("+a two", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("b.st", patch, StringComparison.Ordinal);

        // No commit header in front of it: the parser and the highlighting downstream read a
        // unified diff, which is why --format= is passed at all.
        Assert.StartsWith("diff --git", patch.TrimStart(), StringComparison.Ordinal);

        var file = Assert.Single(
            await Client.CommitFilesAsync(head.Id, CancellationToken.None),
            f => f.Path == "a.st");
        Assert.Equal(ChangeKind.Modified, file.Kind);
    }

    /// <summary>
    /// The first commit of a repository has no parent, and is still readable.
    /// </summary>
    /// <remarks>
    /// The reason the client uses <c>git show</c> with a pathspec rather than diffing the
    /// commit against <c>HEAD~1</c>. Scrolling to the bottom of the history is exactly how
    /// somebody reaches this commit, so it cannot be the one that fails.
    /// </remarks>
    [SkippableFact]
    public async Task The_root_commit_has_a_file_list_and_a_patch()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("first.st", "in the beginning\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "the root commit");

        var root = (await Client.RecentCommitsAsync(1, 0, CancellationToken.None))[0];

        var file = Assert.Single(await Client.CommitFilesAsync(root.Id, CancellationToken.None));
        Assert.Equal(ChangeKind.Added, file.Kind);
        Assert.Equal("first.st", file.Path);

        var patch = await Client.CommitFileDiffAsync(root.Id, "first.st", CancellationToken.None);
        Assert.Contains("+in the beginning", patch, StringComparison.Ordinal);
    }

    /// <summary>
    /// A merge yields no file list, and that is git's answer rather than a failure.
    /// </summary>
    /// <remarks>
    /// Git suppresses the combined diff for a merge, so both the patch and the file list come
    /// back empty. The panel says so in a row of its own; what is asserted here is that the
    /// client reports emptiness rather than inventing a parent to diff against, which would
    /// describe a change nobody made.
    /// </remarks>
    [SkippableFact]
    public async Task A_merge_commit_reports_no_files_rather_than_one_parents_diff()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("base.st", "base\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "base");

        await RunRawAsync("checkout", "-b", "side");
        Write("side.st", "side\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "on the side");

        await RunRawAsync("checkout", "main");
        Write("main.st", "main\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "on main");

        await RunRawAsync("merge", "--no-ff", "side", "-m", "merge side into main");

        var head = (await Client.RecentCommitsAsync(1, 0, CancellationToken.None))[0];
        Assert.StartsWith("merge side into main", head.Subject, StringComparison.Ordinal);

        Assert.Empty(await Client.CommitFilesAsync(head.Id, CancellationToken.None));
        Assert.True(string.IsNullOrWhiteSpace(
            await Client.CommitPatchAsync(head.Id, CancellationToken.None)));
    }

    // --- stash --------------------------------------------------------------------

    [SkippableFact]
    public async Task The_stash_format_round_trips_and_reverts_the_working_tree()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "committed\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", "edited\n");

        var pushed = await Client.StashPushAsync(
            "retune: the PID", includeUntracked: false, CancellationToken.None);
        Assert.True(pushed.Success, pushed.FailureReason);

        // The whole point of a stash: the working tree goes back to HEAD.
        Assert.Equal("committed\n", Read("a.st"));

        var stashes = await Client.StashListAsync(CancellationToken.None);
        foreach (var s in stashes)
        {
            output.WriteLine($"{s.Ref} [{s.Branch}] {s.Message} ({s.Age}) <- {s.Subject}");
        }

        var stash = Assert.Single(stashes);
        Assert.Equal("stash@{0}", stash.Ref);
        Assert.Equal("main", stash.Branch);

        // A message containing a colon is the case a naive split truncates, and `git stash
        // push -m` is how somebody writes one.
        Assert.Equal("retune: the PID", stash.Message);
        Assert.NotEmpty(stash.Age);
    }

    [SkippableFact]
    public async Task A_plain_stash_reports_the_branch_it_was_taken_on()
    {
        // No message, so git writes its "WIP on <branch>: <sha> <subject>" shape - the one
        // the parser has to strip an abbreviated commit id out of.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "committed\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "the last commit subject");

        Write("a.st", "edited\n");
        await Client.StashPushAsync(null, includeUntracked: false, CancellationToken.None);

        var stash = Assert.Single(await Client.StashListAsync(CancellationToken.None));
        output.WriteLine(stash.Subject);

        Assert.Equal("main", stash.Branch);
        Assert.Equal("the last commit subject", stash.Message);
    }

    [SkippableFact]
    public async Task Apply_keeps_the_stash_and_pop_removes_it()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "committed\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", "edited\n");
        await Client.StashPushAsync("work", includeUntracked: false, CancellationToken.None);

        var applied = await Client.StashApplyAsync("stash@{0}", drop: false, CancellationToken.None);
        Assert.True(applied.Success, applied.FailureReason);
        Assert.Equal("edited\n", Read("a.st"));
        Assert.Single(await Client.StashListAsync(CancellationToken.None));

        // Pop needs a clean tree to restore into, and the apply above dirtied it.
        await RunRawAsync("checkout", "--", "a.st");

        var popped = await Client.StashApplyAsync("stash@{0}", drop: true, CancellationToken.None);
        Assert.True(popped.Success, popped.FailureReason);
        Assert.Equal("edited\n", Read("a.st"));
        Assert.Empty(await Client.StashListAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task An_untracked_file_is_stashed_only_when_asked_for()
    {
        // Off by default because it sweeps up build output and an export tree that was never
        // committed - which would look, to the person who pressed Stash, like Versio deleting
        // their mirror.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "committed\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("scratch.txt", "not committed\n");
        Write("a.st", "edited\n");

        await Client.StashPushAsync(null, includeUntracked: false, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_repo, "scratch.txt")));

        await RunRawAsync("stash", "pop");
        await Client.StashPushAsync(null, includeUntracked: true, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_repo, "scratch.txt")));
    }

    [SkippableFact]
    public async Task Dropping_a_stash_removes_it_from_the_list()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "committed\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", "edited\n");
        await Client.StashPushAsync("doomed", includeUntracked: false, CancellationToken.None);

        var dropped = await Client.StashDropAsync("stash@{0}", CancellationToken.None);

        Assert.True(dropped.Success, dropped.FailureReason);
        Assert.Empty(await Client.StashListAsync(CancellationToken.None));
    }

    // --- per-hunk staging ---------------------------------------------------------

    /// <summary>Ten numbered lines, far enough apart to make two independent hunks.</summary>
    private const string Original =
        "line 1\nline 2\nline 3\nline 4\nline 5\n" +
        "line 6\nline 7\nline 8\nline 9\nline 10\n" +
        "line 11\nline 12\nline 13\nline 14\nline 15\n" +
        "line 16\nline 17\nline 18\nline 19\nline 20\n";

    private const string EditedTwice =
        "line 1\nline 2 CHANGED\nline 3\nline 4\nline 5\n" +
        "line 6\nline 7\nline 8\nline 9\nline 10\n" +
        "line 11\nline 12\nline 13\nline 14\nline 15\n" +
        "line 16\nline 17\nline 18 CHANGED\nline 19\nline 20\n";

    [SkippableFact]
    public async Task One_hunk_can_be_staged_leaving_the_other_in_the_working_tree()
    {
        // The feature, end to end: git produces the diff, the builder cuts one hunk out of
        // it, and git accepts the result. Everything in between is only provable this way.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", Original);
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", EditedTwice);

        var diff = await Client.DiffAsync("a.st", staged: false, CancellationToken.None);
        output.WriteLine(diff);

        Assert.Equal(2, HunkPatchBuilder.Count(diff));

        var patch = HunkPatchBuilder.Build(diff, 0);
        Assert.NotNull(patch);
        output.WriteLine("--- patch ---");
        output.WriteLine(patch);

        var applied = await Client.ApplyToIndexAsync(patch!, reverse: false, CancellationToken.None);
        Assert.True(applied.Success, applied.FailureReason);

        // The index has the first change and not the second.
        var staged = await Client.DiffAsync("a.st", staged: true, CancellationToken.None);
        Assert.Contains("line 2 CHANGED", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("line 18 CHANGED", staged, StringComparison.Ordinal);

        // And the working tree is untouched: the file on disk still says what was typed.
        Assert.Equal(EditedTwice, Read("a.st"));

        // What is left unstaged is the other hunk, and only that.
        var remaining = await Client.DiffAsync("a.st", staged: false, CancellationToken.None);
        Assert.Equal(1, HunkPatchBuilder.Count(remaining));
        Assert.Contains("line 18 CHANGED", remaining, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_staged_hunk_can_be_taken_back_out_of_the_index()
    {
        // Unstaging is the same patch applied in reverse. Getting the direction wrong would
        // silently do the opposite of what the button said, so it is asserted rather than
        // assumed to follow from the staging case above.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", Original);
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", EditedTwice);
        await RunRawAsync("add", "-A");

        var staged = await Client.DiffAsync("a.st", staged: true, CancellationToken.None);
        Assert.Equal(2, HunkPatchBuilder.Count(staged));

        var patch = HunkPatchBuilder.Build(staged, 1);
        var applied = await Client.ApplyToIndexAsync(patch!, reverse: true, CancellationToken.None);
        Assert.True(applied.Success, applied.FailureReason);

        var remaining = await Client.DiffAsync("a.st", staged: true, CancellationToken.None);
        Assert.Contains("line 2 CHANGED", remaining, StringComparison.Ordinal);
        Assert.DoesNotContain("line 18 CHANGED", remaining, StringComparison.Ordinal);

        Assert.Equal(EditedTwice, Read("a.st"));
    }

    [SkippableFact]
    public async Task A_later_edit_to_the_file_does_not_change_what_a_staged_hunk_stages()
    {
        // Worth pinning because the first version of this test asserted the opposite and
        // passed nothing. `git apply --cached` reads the INDEX as the pre-image, not the
        // working tree - so a save landing between the diff being read and the button being
        // pressed does not invalidate the patch, and what gets staged is still exactly the
        // hunk that was on screen.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", Original);
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", EditedTwice);
        var diff = await Client.DiffAsync("a.st", staged: false, CancellationToken.None);

        // The file moves on underneath the diff we are holding.
        Write("a.st", "something else entirely\n");

        var patch = HunkPatchBuilder.Build(diff, 0);
        var applied = await Client.ApplyToIndexAsync(patch!, reverse: false, CancellationToken.None);

        Assert.True(applied.Success, applied.FailureReason);

        // The index got the hunk the user clicked, and nothing from the newer edit.
        var staged = await Client.DiffAsync("a.st", staged: true, CancellationToken.None);
        Assert.Contains("line 2 CHANGED", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("something else entirely", staged, StringComparison.Ordinal);

        // And the newer edit is still on disk, untouched.
        Assert.Equal("something else entirely\n", Read("a.st"));
    }

    [SkippableFact]
    public async Task A_hunk_is_refused_when_the_index_moved_out_from_under_it()
    {
        // The staleness that does matter, and the reason --unidiff-zero is not passed. Git's
        // context check is the only thing standing between a patch and the wrong offset, and
        // a refusal the user can read beats a corruption they cannot.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", Original);
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", EditedTwice);
        var diff = await Client.DiffAsync("a.st", staged: false, CancellationToken.None);

        // Something else stages a wholly different file - the index is no longer what the
        // diff was computed against.
        Write("a.st", "one line, nothing like the original\n");
        await RunRawAsync("add", "-A");

        var patch = HunkPatchBuilder.Build(diff, 0);
        var applied = await Client.ApplyToIndexAsync(patch!, reverse: false, CancellationToken.None);

        output.WriteLine(applied.FailureReason ?? "(no failure)");
        Assert.False(applied.Success);
        Assert.NotNull(applied.FailureReason);
    }

    [SkippableFact]
    public async Task A_patch_carrying_non_ascii_content_survives_the_pipe()
    {
        // The same class of bug as the mangled subprocess output: a patch goes in over
        // standard input, and encoding it with the console codepage would hand git something
        // that no longer matches the file it came from. CODESYS object names routinely
        // contain umlauts.
        Skip.IfNot(GitAvailable, "git not on PATH");
        await InitAsync();

        Write("a.st", "(* Kuehlschrank *)\nline 2\nline 3\n");
        await RunRawAsync("add", "-A");
        await RunRawAsync("commit", "-m", "first");

        Write("a.st", "(* Kühlschrank Regelung *)\nline 2\nline 3\n");

        var diff = await Client.DiffAsync("a.st", staged: false, CancellationToken.None);
        var patch = HunkPatchBuilder.Build(diff, 0);

        var applied = await Client.ApplyToIndexAsync(patch!, reverse: false, CancellationToken.None);
        Assert.True(applied.Success, applied.FailureReason);

        var staged = await Client.DiffAsync("a.st", staged: true, CancellationToken.None);
        Assert.Contains("Kühlschrank Regelung", staged, StringComparison.Ordinal);
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
