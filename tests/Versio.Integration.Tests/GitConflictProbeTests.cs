using Versio.Infrastructure.Git;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// What the sync engine is told about a merge in progress, from git rather than from a fixture.
/// </summary>
/// <remarks>
/// <para>
/// The conflict gate refuses both sync directions while the tree is mid-merge, and everything it
/// decides rests on this one answer. Asserting it against a hand-written status string would be
/// asserting what somebody remembered git prints; a real conflict is cheap to make, so there is
/// no reason to guess.
/// </para>
/// <para>
/// The unmerged entry has its own shape in porcelain v2 — <c>u</c> rather than <c>1</c>, with two
/// stage columns — which is exactly the kind of thing a remembered fixture gets subtly wrong.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GitConflictProbeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "Versio", "conflict", Guid.NewGuid().ToString("N")[..8]);

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

    private Task<IReadOnlyList<string>> UnmergedAsync() =>
        new GitConflictProbe(new GitCliClient(_repo, logger: new TestLogger(output)))
            .UnmergedPathsAsync(CancellationToken.None);

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_repo))
            {
                return;
            }

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

    private async Task RunAsync(params string[] args)
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

    /// <summary>Two branches that changed the same line, merged.</summary>
    private async Task MakeAConflictAsync(string path)
    {
        Directory.CreateDirectory(_repo);

        await RunAsync("init", "-b", "main");
        await RunAsync("config", "user.email", "test@versio.local");
        await RunAsync("config", "user.name", "Versio Test");

        Write(path, "shared\noriginal\n");
        await RunAsync("add", ".");
        await RunAsync("commit", "-m", "base");

        await RunAsync("checkout", "-b", "theirs");
        Write(path, "shared\ntheir version\n");
        await RunAsync("commit", "-am", "theirs");

        await RunAsync("checkout", "main");
        Write(path, "shared\nour version\n");
        await RunAsync("commit", "-am", "ours");

        // Expected to fail. That failure is the state under test.
        await RunAsync("merge", "theirs");
    }

    [SkippableFact]
    public async Task A_clean_tree_reports_nothing()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");

        Directory.CreateDirectory(_repo);
        await RunAsync("init", "-b", "main");
        await RunAsync("config", "user.email", "test@versio.local");
        await RunAsync("config", "user.name", "Versio Test");
        Write("plc/A/Simulation.st", "PROGRAM A END_PROGRAM");
        await RunAsync("add", ".");
        await RunAsync("commit", "-m", "first");

        Assert.Empty(await UnmergedAsync());
    }

    [SkippableFact]
    public async Task A_real_merge_conflict_is_reported_by_path()
    {
        Skip.IfNot(GitAvailable, "git not on PATH");

        await MakeAConflictAsync("plc/A/Simulation.st");

        var unmerged = await UnmergedAsync();

        Assert.Equal("plc/A/Simulation.st", Assert.Single(unmerged));
    }

    [SkippableFact]
    public async Task Resolving_the_merge_clears_it()
    {
        // The reason nothing is cached. A merge is resolved while Versio is running, and an
        // answer held from a minute ago would keep refusing syncs after the user had finished.
        Skip.IfNot(GitAvailable, "git not on PATH");

        await MakeAConflictAsync("plc/A/Simulation.st");

        Assert.NotEmpty(await UnmergedAsync());

        Write("plc/A/Simulation.st", "shared\nresolved by hand\n");
        await RunAsync("add", "plc/A/Simulation.st");

        Assert.Empty(await UnmergedAsync());
    }

    [SkippableFact]
    public async Task A_dirty_tree_that_is_not_mid_merge_reports_nothing()
    {
        // Uncommitted changes are not conflicts. Confusing the two would halt syncing for the
        // ordinary state of working on something.
        Skip.IfNot(GitAvailable, "git not on PATH");

        Directory.CreateDirectory(_repo);
        await RunAsync("init", "-b", "main");
        await RunAsync("config", "user.email", "test@versio.local");
        await RunAsync("config", "user.name", "Versio Test");
        Write("plc/A/Simulation.st", "PROGRAM A END_PROGRAM");
        await RunAsync("add", ".");
        await RunAsync("commit", "-m", "first");

        Write("plc/A/Simulation.st", "PROGRAM A (* edited *) END_PROGRAM");
        Write("plc/A/New.st", "PROGRAM N END_PROGRAM");

        Assert.Empty(await UnmergedAsync());
    }
}
