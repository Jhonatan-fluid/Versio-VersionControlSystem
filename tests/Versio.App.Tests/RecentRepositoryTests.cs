using System.Diagnostics;
using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Settings;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The repository dropdown remembers, and says something useful when a repository has gone.
/// </summary>
/// <remarks>
/// <para>
/// Reported from the app: the dropdown listed one repository — the one the app had opened by
/// itself — so the control for jumping between repositories could not jump anywhere. Nothing
/// wrote the list down and nothing read it back; it was an <c>ObservableCollection</c> filled
/// on open and dropped on exit.
/// </para>
/// <para>
/// These come in through <see cref="ShellViewModel.OpenAsync"/> and
/// <see cref="ShellViewModel.SwitchRepositoryAsync"/> — where a user comes in — rather than
/// through the store. This repository has now been bitten three times by a method that was
/// perfectly tested and called by nothing, and a persistence test that only exercises the
/// store would be the fourth.
/// </para>
/// </remarks>
public sealed class RecentRepositoryTests
{
    /// <summary>An <see cref="IAppSettingsStore"/> that keeps the file in memory.</summary>
    /// <remarks>
    /// The rules are tested in Core against no store at all; what these tests need is to see
    /// that something was actually written, and to hand it back to a second shell.
    /// </remarks>
    private sealed class MemoryStore : IAppSettingsStore
    {
        public AppSettings Saved { get; private set; } = AppSettings.Empty;

        public int Writes { get; private set; }

        public Task<AppSettings> ReadAsync(CancellationToken ct) => Task.FromResult(Saved);

        public Task WriteAsync(AppSettings settings, CancellationToken ct)
        {
            Saved = settings;
            Writes++;
            return Task.CompletedTask;
        }
    }

    private static ShellViewModel Build(MemoryStore store)
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        // No toolchain: opening a repository adopts any project in it and would launch the
        // real CODESYS. A machine without one is a state the app handles and reports.
        return new ShellViewModel(
            logger, new LogViewModel(sink), toolchains: (_, _) => null, settings: store);
    }

    [SkippableFact]
    public async Task The_dropdown_selects_the_open_repository_however_its_path_was_spelled()
    {
        // THE DEFECT: the list had one row and nothing was selected, so the control rendered
        // blank over a perfectly good list. Found by launching the app with a forward-slash
        // path — which is also what `git rev-parse --show-toplevel` answers, so this is
        // reachable without anybody typing anything unusual.
        //
        // The recent list canonicalises on the way IN, so it correctly refuses to store the
        // same repository twice. The dropdown then looked it up with an ordinal ==, and the
        // stored spelling and the opened spelling are not the same string.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        var store = new MemoryStore();

        using var shell = Build(store);
        await shell.OpenAsync(repo.Path.Replace('\\', '/'));

        // One row, because the promotion canonicalises — that half already worked.
        var row = Assert.Single(shell.RecentRepositories);

        Assert.NotNull(shell.SelectedRepository);
        Assert.Same(row, shell.SelectedRepository);
    }

    [SkippableFact]
    public async Task Opening_a_repository_writes_it_down_and_a_new_window_lists_it()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        var store = new MemoryStore();

        var first = Build(store);
        await first.OpenAsync(repo.Path);

        var remembered = Assert.Single(store.Saved.Recent);
        Assert.Equal(repo.Path, remembered.Path, ignoreCase: true);

        // The part that was missing entirely: a second window, with nothing opened in it,
        // still knows where the user has been.
        var second = Build(store);
        await second.LoadRecentAsync();

        Assert.Equal(repo.Path, Assert.Single(second.RecentRepositories).Path, ignoreCase: true);
    }

    [SkippableFact]
    public async Task The_remote_is_recorded_while_the_repository_is_still_there()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        repo.Run("remote add origin https://example.com/machine.git");

        var store = new MemoryStore();
        await Build(store).OpenAsync(repo.Path);

        // Read at open time on purpose. When it is wanted — offering to clone a vanished
        // repository back — there is no .git left to read it out of.
        Assert.Equal("https://example.com/machine.git", Assert.Single(store.Saved.Recent).RemoteUrl);
    }

    [SkippableFact]
    public async Task A_repository_with_no_remote_records_none_and_is_still_remembered()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        var store = new MemoryStore();

        await Build(store).OpenAsync(repo.Path);

        Assert.Equal(string.Empty, Assert.Single(store.Saved.Recent).RemoteUrl);
    }

    [Fact]
    public async Task Picking_a_repository_that_has_gone_asks_what_to_do_about_it()
    {
        var store = new MemoryStore();
        using var shell = Build(store);
        MissingRepositoryPrompt? asked = null;

        shell.Asked().MissingRepository = prompt =>
        {
            asked = prompt;
            return MissingRepositoryChoice.Cancel;
        };

        await shell.SwitchRepositoryAsync(
            new RepoEntry(@"C:\nowhere\gone", "https://example.com/gone.git"));

        Assert.NotNull(asked);
        Assert.Contains(MissingRepositoryChoice.CloneAgain, asked!.Choices);

        // Cancel leaves the entry alone: a drive that is not mounted today is mounted
        // tomorrow, and this row is the only record of where the repository was.
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Removing_a_missing_repository_takes_it_out_of_the_list_and_the_file()
    {
        var store = new MemoryStore();
        using var shell = Build(store);

        await store.WriteAsync(
            new AppSettings
            {
                Recent =
                [
                    new RecentRepository { Path = @"C:\nowhere\gone" },
                    new RecentRepository { Path = @"C:\nowhere\other" },
                ],
            },
            CancellationToken.None);

        await shell.LoadRecentAsync();
        Assert.Equal(2, shell.RecentRepositories.Count);

        shell.Asked().MissingRepository = _ => MissingRepositoryChoice.Forget;
        await shell.SwitchRepositoryAsync(new RepoEntry(@"C:\nowhere\gone"));

        Assert.Equal(@"C:\nowhere\other", Assert.Single(store.Saved.Recent).Path);
        Assert.Equal(@"C:\nowhere\other", Assert.Single(shell.RecentRepositories).Path);
    }

    [Fact]
    public async Task No_view_attached_means_cancel_rather_than_forget()
    {
        // The same rule AskGuard follows. A wiring mistake must not take the branch that
        // throws something away.
        var store = new MemoryStore();
        using var shell = Build(store);

        await store.WriteAsync(
            new AppSettings { Recent = [new RecentRepository { Path = @"C:\nowhere\gone" }] },
            CancellationToken.None);

        await shell.LoadRecentAsync();
        await shell.SwitchRepositoryAsync(new RepoEntry(@"C:\nowhere\gone"));

        Assert.Single(store.Saved.Recent);
    }

    [SkippableFact]
    public async Task The_most_recent_repository_still_on_disk_is_what_startup_falls_back_to()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        var store = new MemoryStore();

        await store.WriteAsync(
            new AppSettings
            {
                Recent =
                [
                    new RecentRepository { Path = @"C:\nowhere\gone" },
                    new RecentRepository { Path = repo.Path },
                ],
            },
            CancellationToken.None);

        using var shell = Build(store);
        await shell.LoadRecentAsync();

        // The one that is gone is skipped rather than removed, and the fallback keeps looking.
        Assert.Equal(repo.Path, shell.MostRecentExisting());
    }

    [SkippableFact]
    public async Task A_shell_built_without_a_store_writes_nothing_to_the_users_settings()
    {
        // This is not hypothetical. The view model defaulted to the real store, so every
        // test that opened a throwaway repository rewrote the developer's own recent list -
        // two versio-reset-* temp folders were sitting in it when this was found. The real
        // store is wired by the application host and by nothing else.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VersioUser", "settings.json");

        var before = File.Exists(real) ? File.ReadAllBytes(real) : null;

        using var repo = new TempRepo();
        var sink = new InMemoryLogSink();
        using var shell = new ShellViewModel(
            new VersioLogger(new SystemClock()).AddSink(sink),
            new LogViewModel(sink),
            toolchains: (_, _) => null);

        await shell.OpenAsync(repo.Path);

        var after = File.Exists(real) ? File.ReadAllBytes(real) : null;

        Assert.Equal(before is null, after is null);
        if (before is not null && after is not null)
        {
            Assert.Equal(before, after);
        }

        // In-session behaviour is unchanged: the dropdown still lists what was opened, it is
        // only the writing down that the null store declines to do.
        Assert.Contains(shell.RecentRepositories, r => r.Path.Equals(
            repo.Path, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Opening_a_repository_tells_the_watcher_what_git_ignores()
    {
        // The wiring, from where the user comes in. The filter's own tests prove the rule and
        // say nothing about whether anything supplies the globs - and a rule that is correct
        // and never delivered is the failure this repository keeps finding.
        //
        // What it costs when it is not delivered was measured in the running app: CODESYS
        // rewrites <project>.~u every few seconds while the IDE holds the project, and every
        // touch was spending a git status on a file git had been told to ignore.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        File.WriteAllText(PathIn(repo, "Machine.project"), "binary-ish");

        using var shell = Build(new MemoryStore());
        await shell.OpenAsync(repo.Path);

        Assert.Contains(shell.WatcherIgnoredGlobs, g => g.EndsWith("Machine.~u*", StringComparison.Ordinal));
        Assert.Contains(shell.WatcherIgnoredGlobs, g => g.EndsWith("Machine-*.opt", StringComparison.Ordinal));

        // And Versio's own scratch directory, which it writes to constantly.
        Assert.Contains(shell.WatcherIgnoredGlobs, g => g.StartsWith(".versio/local/", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Opening_a_repository_puts_back_a_mirror_a_killed_export_left_in_the_trash()
    {
        // The dangerous combination, arranged on disk and driven through OpenAsync: the mirror
        // is gone and its last complete copy is in a trash directory beside it, which is what
        // a kill between the swap's two renames leaves. A sweep that deleted leftovers would
        // destroy it - quietly, at startup, in a repository nobody had touched.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();

        // A tracked project with a mirror, so there is a binding to sweep.
        File.WriteAllText(PathIn(repo, "Machine.project"), "binary-ish");
        repo.Run("add .");
        repo.Run("commit -qm project");

        var mirror = System.IO.Path.Combine(repo.Path, "Machine");
        var trash = mirror + Versio.Core.Bindings.BindingConventions.TrashSuffix;

        Directory.CreateDirectory(trash);
        File.WriteAllText(System.IO.Path.Combine(trash, "Main.st"), "PROGRAM Main");

        using var shell = Build(new MemoryStore());
        await shell.OpenAsync(repo.Path);

        Assert.True(Directory.Exists(mirror), "the mirror was not restored from the trash");
        Assert.False(Directory.Exists(trash), "the trash directory should be gone once restored");
        Assert.Equal("PROGRAM Main",
                     File.ReadAllText(System.IO.Path.Combine(mirror, "Main.st")));
    }

    [SkippableFact]
    public async Task A_repository_adopted_before_a_rule_existed_gets_it_on_the_next_open()
    {
        // Adoption writes the git rules once, and the scan skips a project that is already
        // bound - so a rule added to Versio after a repository was adopted never reached it.
        // Found on a real repository: its mirror had no eol rule, its machine had
        // core.autocrlf=true, and every file in it would have hashed differently from what an
        // export produces the moment git touched them.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        File.WriteAllText(PathIn(repo, "Machine.project"), "binary-ish");

        // Adopted by the first open, which writes the rules as they are today.
        await Build(new MemoryStore()).OpenAsync(repo.Path);

        var attributes = System.IO.Path.Combine(repo.Path, ".gitattributes");
        var ignore = System.IO.Path.Combine(repo.Path, ".gitignore");
        Assert.True(File.Exists(attributes));

        // Now put the repository back the way an OLDER Versio would have left it: bound, with
        // the project rules but nothing about the mirror.
        File.WriteAllText(
            attributes,
            "*.project binary -diff -merge" + Environment.NewLine +
            "*.library binary -diff -merge" + Environment.NewLine);

        File.WriteAllText(ignore, ".versio/local/" + Environment.NewLine);

        await Build(new MemoryStore()).OpenAsync(repo.Path);

        var topped = File.ReadAllText(attributes);
        Assert.Contains("text eol=lf", topped, StringComparison.Ordinal);
        Assert.Contains("Machine/**", topped, StringComparison.Ordinal);

        // And the rules that were already there are left exactly as they were.
        Assert.Contains("*.project binary -diff -merge", topped, StringComparison.Ordinal);

        // The ignore plan is topped up the same way, with the directories an export swaps
        // through - which a killed export would otherwise leave stageable.
        var ignores = File.ReadAllText(ignore);
        Assert.Contains(".versio_staging/", ignores, StringComparison.Ordinal);
        Assert.Contains(".versio_trash/", ignores, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Topping_up_twice_changes_nothing_the_second_time()
    {
        // It runs on every scan, so a repository already covered must come out byte-identical
        // - otherwise every open would put a diff in front of somebody with nothing in it.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        File.WriteAllText(PathIn(repo, "Machine.project"), "binary-ish");

        await Build(new MemoryStore()).OpenAsync(repo.Path);

        var attributes = System.IO.Path.Combine(repo.Path, ".gitattributes");
        var first = File.ReadAllBytes(attributes);

        await Build(new MemoryStore()).OpenAsync(repo.Path);

        Assert.Equal(first, File.ReadAllBytes(attributes));
    }

    [SkippableFact]
    public async Task A_tia_program_store_goes_through_lfs_and_its_mirror_stays_text()
    {
        // Reported as a question, found as a gap: adoption wrote a rule for *.ap21 - the
        // 150 KB pointer - and nothing for System/PEData.plf, the half-gigabyte program beside
        // it, which a commit then stored as an ordinary git blob.
        //
        // Asked of real git rather than of the file's text, because what matters is what git
        // resolves - and this repository's own hand-written TIA rules read correctly while
        // resolving wrong (merge=lfs on the binaries, a mirror that diffed as binary).
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        Directory.CreateDirectory(PathIn(repo, "Station/System"));
        File.WriteAllText(PathIn(repo, "Station/Station.ap21"), "pointer");
        File.WriteAllText(PathIn(repo, "Station/System/PEData.plf"), "program");

        using (var shell = Build(new MemoryStore()))
        {
            await shell.OpenAsync(repo.Path);
        }

        var store = CheckAttr(repo, "Station/System/PEData.plf");
        Assert.Equal("unset", store["merge"]);
        Assert.Equal("unset", store["text"]);
        if (LfsIsAvailable())
        {
            Assert.Equal("lfs", store["filter"]);
        }

        // The mirror sits INSIDE the project folder. That is why the rules are per store
        // folder: one rule over Station/** would have made this binary.
        var mirror = CheckAttr(repo, "Station/Station/FB_Main.scl");
        Assert.Equal("set", mirror["text"]);
        Assert.Equal("unspecified", mirror["diff"]);
        Assert.Equal("unspecified", mirror["merge"]);
        Assert.Equal("unspecified", mirror["filter"]);
    }

    [SkippableFact]
    public async Task A_tia_repository_adopted_before_the_store_rules_existed_gets_them_on_open()
    {
        // The top-up half. Adoption writes rules once and the scan skips a project that is
        // already bound, so every TIA repository adopted before this rule would have kept
        // committing PEData.plf outside LFS forever. The open-time top-up has to know the
        // PROJECTS, not only their mirrors, because the store is scoped to the project.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        Directory.CreateDirectory(PathIn(repo, "Station/System"));
        File.WriteAllText(PathIn(repo, "Station/Station.ap21"), "pointer");
        File.WriteAllText(PathIn(repo, "Station/System/PEData.plf"), "program");

        using (var shell = Build(new MemoryStore()))
        {
            await shell.OpenAsync(repo.Path);
        }

        // Back to what an older Versio left: the extension rule and the mirror rule only.
        var attributes = PathIn(repo, ".gitattributes");
        File.WriteAllText(attributes,
            "*.ap21 binary -diff -merge" + Environment.NewLine +
            "Station/Station/** text eol=lf" + Environment.NewLine);

        using (var shell = Build(new MemoryStore()))
        {
            await shell.OpenAsync(repo.Path);
        }

        var topped = File.ReadAllText(attributes);
        Assert.Contains("Station/System/**", topped, StringComparison.Ordinal);
        Assert.Contains("*.ap21 binary -diff -merge", topped, StringComparison.Ordinal);
        Assert.Equal("unset", CheckAttr(repo, "Station/System/PEData.plf")["merge"]);
    }

    [SkippableFact]
    public async Task Adopting_a_tia_project_writes_its_store_rules_itself()
    {
        // The adoption half, called directly - and it has to be. Removing the project path
        // from AdoptAsync's attribute call left the shell-level test above green, because the
        // open-time top-up runs straight after adoption and writes the same rules. So that
        // test watches the top-up, and nothing watched adoption: the path every non-shell
        // caller of RepoSetup takes.
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        Directory.CreateDirectory(PathIn(repo, "Station/System"));
        File.WriteAllText(PathIn(repo, "Station/Station.ap21"), "pointer");

        var setup = new Versio.Infrastructure.Bindings.RepoSetup(
            repo.Path, Versio.Plc.TiaPortal.TiaPortalProjectFormat.Instance);
        var result = await setup.AdoptAsync(
            "Station/Station.ap21", git: null, profile: null, CancellationToken.None);

        Assert.True(result.Adopted, result.Decision.Explanation);
        Assert.Contains("Station/System/**", result.GitAttributePatterns);
        Assert.Contains("Station/System/**",
            File.ReadAllText(PathIn(repo, ".gitattributes")), StringComparison.Ordinal);
        Assert.Equal("unset", CheckAttr(repo, "Station/System/PEData.plf")["merge"]);

        // And the mirror is still text: adoption hands the planner the mirror it just chose.
        Assert.Equal("set", CheckAttr(repo, "Station/Station/FB_Main.scl")["text"]);
    }

    /// <summary>What git resolves for one path: attribute name to its reported value.</summary>
    private static Dictionary<string, string> CheckAttr(TempRepo repo, string path)
    {
        using var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"check-attr text diff merge filter -- \"{path}\"",
            WorkingDirectory = repo.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        var output = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();

        // "Station/System/PEData.plf: merge: unset" - the value is after the last ": ".
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(": "))
            .Where(parts => parts.Length >= 3)
            .ToDictionary(parts => parts[^2], parts => parts[^1], StringComparer.Ordinal);
    }

    private static bool LfsIsAvailable()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "lfs version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            git!.WaitForExit();
            return git.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string PathIn(TempRepo repo, string name) =>
        System.IO.Path.Combine(repo.Path, name);

    // ------------------------------------------------------------------- real git plumbing

    /// <summary>A throwaway git repository, deleted when the test ends.</summary>
    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "versio-recent-" + Guid.NewGuid().ToString("N")[..8]);

        public TempRepo()
        {
            Directory.CreateDirectory(Path);
            Run("init -q .");
            Run("config user.email test@versio");
            Run("config user.name test");
            File.WriteAllText(System.IO.Path.Combine(Path, "readme.md"), "contents");
            Run("add .");
            Run("commit -qm first");
        }

        public void Run(string arguments)
        {
            using var git = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            git!.WaitForExit();
        }

        public void Dispose()
        {
            try
            {
                // .git holds read-only objects on Windows, which Delete refuses to remove.
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

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
