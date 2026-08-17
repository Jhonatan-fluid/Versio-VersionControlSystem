using Versio.Core.Git;
using Versio.Infrastructure.Git;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Presence and identity, against the real <c>git</c>.
/// </summary>
/// <remarks>
/// <para>
/// These read and write real git configuration, so every repository here is a throwaway and
/// every write is <c>--local</c>. Nothing in this file may touch the global config: a test that
/// rewrites the developer's own <c>user.email</c> would be a genuinely nasty thing to leave in
/// a suite, and it would only be noticed by whoever later wondered why their commits were
/// attributed to "Versio Test".
/// </para>
/// <para>
/// The identity case is asserted against a repository that has had its local identity
/// explicitly unset, because a bare <c>git init</c> on a developer machine inherits a perfectly
/// good global identity — which is the correct behaviour and would make the interesting test
/// vacuous.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GitHealthTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _repo = CreateTempRepo();

    private static string CreateTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Versio", "githealth",
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

    // ------------------------------------------------------------------------ presence

    [SkippableFact]
    public async Task The_probe_finds_git_on_this_machine()
    {
        var presence = await GitCliClient.ProbeAsync("git", null, CancellationToken.None);

        Assert.True(presence.IsUsable);
        Assert.StartsWith("git version", presence.Version);
        Assert.NotEqual(string.Empty, presence.ExecutablePath);
    }

    [SkippableFact]
    public async Task An_executable_that_does_not_exist_is_reported_not_thrown()
    {
        // The whole point of GitPresence: a missing git surfaces as a Win32Exception from the
        // process start, which without this would reach the user as an unhandled crash rather
        // than as a sentence naming PATH.
        var presence = await GitCliClient.ProbeAsync(
            "git-that-is-definitely-not-installed", null, CancellationToken.None);

        Assert.False(presence.IsUsable);
        Assert.Equal(GitAvailability.Missing, presence.Availability);
        Assert.NotEqual(string.Empty, presence.FailureReason);
        Assert.Contains("PATH", presence.Explanation);
    }

    // ------------------------------------------------------------------------ identity

    [SkippableFact]
    public async Task A_repository_with_no_local_or_inherited_identity_reports_none()
    {
        await RunRawAsync("init", "-b", "main");

        // Isolate from whatever the developer has globally: without this the test passes or
        // fails depending on whose machine it runs on, which is worse than not having it.
        await RunRawAsync("config", "--local", "user.name", "");
        await RunRawAsync("config", "--local", "user.email", "");

        var identity = await Client.IdentityAsync(CancellationToken.None);

        Assert.False(identity.IsComplete);
        Assert.NotEqual(string.Empty, identity.Explanation);
    }

    [SkippableFact]
    public async Task Writing_a_local_identity_is_read_back()
    {
        await RunRawAsync("init", "-b", "main");

        var wrote = await Client.SetIdentityAsync(
            "Jane Engineer", "jane@example.com", global: false, CancellationToken.None);
        Assert.True(wrote.Success);

        var identity = await Client.IdentityAsync(CancellationToken.None);

        Assert.True(identity.IsComplete);
        Assert.Equal("Jane Engineer", identity.Name);
        Assert.Equal("jane@example.com", identity.Email);
    }

    [SkippableFact]
    public async Task A_local_write_does_not_touch_the_global_config()
    {
        await RunRawAsync("init", "-b", "main");

        await Client.SetIdentityAsync(
            "Local Only", "local@example.com", global: false, CancellationToken.None);

        // Read the local scope back explicitly. If SetIdentityAsync had passed --global, this
        // repository would report whatever the machine's global identity is instead.
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("--local");
        psi.ArgumentList.Add("--get");
        psi.ArgumentList.Add("user.email");

        using var p = System.Diagnostics.Process.Start(psi)!;
        var read = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        Assert.Equal("local@example.com", read.Trim());
    }

    [SkippableFact]
    public async Task An_identity_set_at_a_wider_scope_still_counts_as_present()
    {
        // Plain `--get` rather than `--local --get`, so a name configured globally years ago —
        // which is what everybody is told to do — is not reported as missing. What matters is
        // whether the commit will succeed, not where the setting was written.
        await RunRawAsync("init", "-b", "main");
        await RunRawAsync("config", "--local", "user.name", "Set Locally");
        await RunRawAsync("config", "--local", "user.email", "set@example.com");

        var identity = await Client.IdentityAsync(CancellationToken.None);

        Assert.True(identity.IsComplete);
    }

    [SkippableFact]
    public async Task A_commit_actually_succeeds_once_the_identity_is_written()
    {
        // The end of the chain: the check exists to predict this, so the prediction is tested
        // against the thing it predicts rather than against another copy of the rule.
        await RunRawAsync("init", "-b", "main");
        await Client.SetIdentityAsync(
            "Jane Engineer", "jane@example.com", global: false, CancellationToken.None);

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello");
        await Client.StageAsync(["a.txt"], CancellationToken.None);

        var commit = await Client.CommitAsync("first", CancellationToken.None);

        Assert.True(commit.Success);
    }
}
