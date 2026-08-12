using Versio.Core.Git;
using Versio.Infrastructure.Git;
using Xunit;
using Xunit.Abstractions;

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

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
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
