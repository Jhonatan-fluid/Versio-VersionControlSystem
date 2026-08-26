using System.Diagnostics;
using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Switching branch with uncommitted work: stashed on the way out, offered back on the way in.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="ShellViewModel.SwitchBranchAsync"/> against a <b>real git
/// repository</b>, because every interesting failure here is about what git actually did.
/// <c>git stash push</c> on a clean tree exits 0 without creating anything; <c>apply</c>
/// accepts a SHA and <c>drop</c> does not; positions renumber underneath you. A fake git would
/// answer to whichever of those this code happens to believe.
/// </para>
/// <para>
/// The rules themselves are unit-tested in <c>AutoStashTests</c> without a repository at all.
/// What these add is that the shell reaches them, in the order that keeps the work findable:
/// stash, record, release the locks — and abandon everything if the stash did not happen.
/// </para>
/// </remarks>
public sealed class AutoStashFlowTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "versio-stash-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoStashFlowTests()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        Directory.CreateDirectory(_path);
        // -b main: this git's default is master, and the branch name is in every assertion
        // below because it is what the stash record is keyed on.
        Git("init -q -b main .");
        Git("config user.email test@versio");
        Git("config user.name test");
        File.WriteAllText(Path.Combine(_path, "machine.st"), "PROGRAM Main\nEND_PROGRAM\n");
        Git("add .");
        Git("commit -qm first");
        Git("branch other");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string Git(string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        var stdout = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return stdout;
    }

    private ShellViewModel Build()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        // No toolchain: a temp repository with no PLC project in it needs none, and a machine
        // without one is a state the app already handles.
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    /// <summary>A shell over this repository, with one uncommitted edit in the tree.</summary>
    /// <remarks>
    /// <b>No <c>using</c> on the shell here — the caller owns it.</b> This read
    /// <c>using var shell = Build();</c> followed by a <c>return shell</c>, which disposes at
    /// the closing brace and hands the caller a torn-down shell — the same bug
    /// <c>GitPanelChromeTests.Build</c> documents catching in itself. Every test on this
    /// helper ran watcher-less and mostly got away with it; the disposal also raced whatever
    /// the watcher had already scheduled, which is one of the reasons this class flaked
    /// under load and nowhere else.
    /// </remarks>
    private async Task<ShellViewModel> OpenWithDirtyTreeAsync()
    {
        var shell = Build();
        await shell.OpenAsync(_path);

        File.WriteAllText(
            Path.Combine(_path, "machine.st"), "PROGRAM Main\n  (* half a thought *)\nEND_PROGRAM\n");

        await shell.RefreshAsync();
        Assert.NotEmpty(shell.Changes);

        return shell;
    }

    private AutoStashState Record() =>
        new JsonAutoStashStore(_path).ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static BranchEntry Other() =>
        new(new Versio.Core.Git.BranchInfo { Name = "other", IsCurrent = false });

    [SkippableFact]
    public async Task Switching_with_work_in_the_tree_stashes_it_and_records_it()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;

        await shell.SwitchBranchAsync(Other());

        // The work is in git, and the tree it came from is clean.
        Assert.Contains("versio-autostash", Git("stash list"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Git("status --porcelain").Trim());

        // And the record names it by the id git will still answer to after a drop renumbers
        // everything - which is the entire reason %H is in the stash format.
        var entry = Assert.Single(Record().Entries);
        Assert.Equal("main", entry.Branch);
        Assert.Contains(entry.StashCommitId[..7], Git("stash list --format=%H"),
                        StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Coming_back_offers_the_work_and_puts_it_back()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;
        await shell.SwitchBranchAsync(Other());

        ChoicePrompt? offered = null;
        shell.Asked().StashedWork = prompt =>
        {
            offered = prompt;
            return StashedWorkChoice.Restore;
        };

        await shell.SwitchBranchAsync(
            new BranchEntry(new Versio.Core.Git.BranchInfo { Name = "main", IsCurrent = false }));

        Assert.NotNull(offered);
        Assert.Contains("1 changed file", offered!.Body, StringComparison.Ordinal);

        // The edit is back in the file, the stash is gone from git, and so is its record.
        Assert.Contains("half a thought",
                        File.ReadAllText(Path.Combine(_path, "machine.st")),
                        StringComparison.Ordinal);

        Assert.Equal(string.Empty, Git("stash list").Trim());
        Assert.Empty(Record().Entries);
    }

    [SkippableFact]
    public async Task Leaving_it_stashed_changes_nothing_and_offers_again()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;
        await shell.SwitchBranchAsync(Other());

        var asked = 0;
        shell.Asked().StashedWork = _ =>
        {
            asked++;
            return StashedWorkChoice.Leave;
        };

        await shell.SwitchBranchAsync(
            new BranchEntry(new Versio.Core.Git.BranchInfo { Name = "main", IsCurrent = false }));
        await shell.SwitchBranchAsync(Other());
        await shell.SwitchBranchAsync(
            new BranchEntry(new Versio.Core.Git.BranchInfo { Name = "main", IsCurrent = false }));

        // Offered on both arrivals, and still there afterwards. Doing nothing has to be a real
        // answer, or the dialog is a demand rather than an offer.
        Assert.Equal(2, asked);
        Assert.Contains("versio-autostash", Git("stash list"), StringComparison.Ordinal);
        Assert.Single(Record().Entries);
    }

    [SkippableFact]
    public async Task A_host_that_answers_nothing_leaves_the_work_alone()
    {
        // Unset means Leave - not Restore, and certainly not Discard. A wiring mistake must
        // not be able to rewrite somebody's working tree or throw their work away.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;
        await shell.SwitchBranchAsync(Other());

        await shell.SwitchBranchAsync(
            new BranchEntry(new Versio.Core.Git.BranchInfo { Name = "main", IsCurrent = false }));

        Assert.Contains("versio-autostash", Git("stash list"), StringComparison.Ordinal);
        Assert.Single(Record().Entries);
    }

    [SkippableFact]
    public async Task A_stash_dropped_from_a_terminal_is_forgotten_rather_than_offered()
    {
        // An ordinary thing for a git user to do, and it must not read as an error.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;
        await shell.SwitchBranchAsync(Other());

        Git("stash drop");

        var asked = 0;
        shell.Asked().StashedWork = _ => { asked++; return StashedWorkChoice.Leave; };

        await shell.SwitchBranchAsync(
            new BranchEntry(new Versio.Core.Git.BranchInfo { Name = "main", IsCurrent = false }));

        Assert.Equal(0, asked);
        Assert.Empty(Record().Entries);
    }

    [SkippableFact]
    public async Task A_stash_that_was_never_created_abandons_the_switch()
    {
        // git stash push on a clean tree exits 0 and creates nothing. Trusting the exit code
        // would check out the other branch having saved nothing - and if the tree had not been
        // clean, that is somebody's work gone.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = Build();
        await shell.OpenAsync(_path);
        await shell.RefreshAsync();

        // Called directly, because with a clean tree the guard answers Proceed and never asks
        // - so the front door cannot reach this branch, and the branch is the one that decides
        // whether a checkout happens over unsaved work.
        var proceeded = await shell.TakeAutoStashAsync("switching");

        Assert.False(proceeded);
        Assert.Empty(Record().Entries);
        Assert.Equal(string.Empty, Git("stash list").Trim());

        // The NOTIFICATION, not the status line. The refusal used to live only in
        // StatusSummary, where the next background refresh legitimately overwrites it with
        // "No changes" — this assertion flaked on exactly that, and a user reading a second
        // too late lost the sentence the same way. A notification survives the refresh, by
        // the design rule Notifications' own remarks state.
        Assert.Contains(shell.Notifications.Visible,
            n => n.ClipboardText.Contains("Nothing was stashed", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_pull_over_a_dirty_tree_is_guarded_now_too()
    {
        // GuardIntent.Pull existed, was tested, and was deliberately unwired from the day the
        // guard was written, because all of its verdicts resolve to a stash that did not yet
        // exist. A pull is the worst path to meet a dirty tree on: it rewrites the project and
        // its mirror together.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        var asked = 0;

        shell.Asked().Guard = _ =>
        {
            asked++;
            return GuardChoice.Cancel;
        };

        await shell.PullForTestAsync();

        Assert.Equal(1, asked);

        // Cancelled, so the edit is untouched and nothing was stashed.
        Assert.Contains("half a thought",
                        File.ReadAllText(Path.Combine(_path, "machine.st")),
                        StringComparison.Ordinal);

        Assert.Equal(string.Empty, Git("stash list").Trim());
    }

    [SkippableFact]
    public async Task Two_stashes_on_one_branch_are_offered_one_after_the_other()
    {
        // Switching away twice without coming back is ordinary, which is why the records are a
        // list. Offering only the newest per arrival meant the older ones were reachable only
        // by switching away and back again, once per stash, with nothing on screen saying so.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;

        // Two rounds of "edit on main, switch away".
        await shell.SwitchBranchAsync(Other());
        await shell.SwitchBranchAsync(MainBranch());

        File.WriteAllText(
            Path.Combine(_path, "machine.st"),
            "PROGRAM Main\n  (* a second thought *)\nEND_PROGRAM\n");

        await shell.RefreshAsync();
        await shell.SwitchBranchAsync(Other());

        Assert.Equal(2, Record().Entries.Count);

        var prompts = new List<string>();
        shell.Asked().StashedWork = prompt =>
        {
            prompts.Add(prompt.Body);
            return StashedWorkChoice.Leave;
        };

        await shell.SwitchBranchAsync(MainBranch());

        // Both offered, in one arrival, and the first says how many are behind it.
        Assert.Equal(2, prompts.Count);
        Assert.Contains("There are 2 saved", prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("There are", prompts[1], StringComparison.Ordinal);

        // Left alone means left alone: both are still in git and still recorded.
        Assert.Equal(2, Record().Entries.Count);
    }

    [SkippableFact]
    public async Task Restoring_one_stops_offering_the_rest_until_the_next_arrival()
    {
        // A second pop on top of the first would land two sets of edits in one working tree
        // with no way to tell them apart - and the refs captured before the first restore
        // name the wrong entries once a pop has renumbered them.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var shell = await OpenWithDirtyTreeAsync();
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;

        await shell.SwitchBranchAsync(Other());
        await shell.SwitchBranchAsync(MainBranch());

        File.WriteAllText(
            Path.Combine(_path, "machine.st"),
            "PROGRAM Main\n  (* a second thought *)\nEND_PROGRAM\n");

        await shell.RefreshAsync();
        await shell.SwitchBranchAsync(Other());

        var asked = 0;
        shell.Asked().StashedWork = _ =>
        {
            asked++;
            return StashedWorkChoice.Restore;
        };

        await shell.SwitchBranchAsync(MainBranch());

        Assert.Equal(1, asked);
        Assert.Single(Record().Entries);
    }

    private static BranchEntry MainBranch() =>
        new(new Versio.Core.Git.BranchInfo { Name = "main", IsCurrent = false });

    private static bool GitIsAvailable()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            git!.WaitForExit();
            return git.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
