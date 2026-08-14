using Versio.Infrastructure.Git;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// Publishing a branch, against real git and a real bare remote.
/// </summary>
/// <remarks>
/// A bare repository on disk is a perfectly good remote and needs no network, credentials or
/// server — which makes the whole push path testable without any of the three. What it cannot
/// cover is authentication, and that is deliberately left to the credential work rather than
/// faked here.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PublishBranchTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Versio", "publish", Guid.NewGuid().ToString("N")[..8]);

    private string Work => Path.Combine(_root, "work");

    private string Remote => Path.Combine(_root, "remote.git");

    private void Setup()
    {
        Directory.CreateDirectory(Remote);
        Git(Remote, "init -q --bare -b main");

        Directory.CreateDirectory(Work);
        Git(Work, "init -q -b main .");
        Git(Work, "config --local user.name TestUser");
        Git(Work, "config --local user.email test@versio.local");
        File.WriteAllText(Path.Combine(Work, "a.txt"), "hello\n");
        Git(Work, "add .");
        Git(Work, "commit -qm first");
    }

    private static void Git(string cwd, string arguments)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        git!.WaitForExit();
    }

    private GitCliClient Client => new(Work, logger: new TestLogger(output));

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [SkippableFact]
    public async Task A_repository_with_no_remote_lists_none()
    {
        Setup();

        Assert.Empty(await Client.RemotesAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Remotes_are_listed_once_added()
    {
        Setup();
        Git(Work, $"remote add origin \"{Remote}\"");
        Git(Work, $"remote add backup \"{Remote}\"");

        var remotes = await Client.RemotesAsync(CancellationToken.None);

        Assert.Equal(2, remotes.Count);
        Assert.Contains("origin", remotes);
        Assert.Contains("backup", remotes);
    }

    [SkippableFact]
    public async Task A_plain_push_fails_without_an_upstream()
    {
        // The whole reason Publish is a separate command rather than a flag on Push. If this
        // ever starts succeeding — because a future git defaults push.autoSetupRemote on — the
        // separation is still right, but this test is where that change gets noticed.
        Setup();
        Git(Work, $"remote add origin \"{Remote}\"");

        var result = await Client.PushAsync(null, CancellationToken.None);

        Assert.False(result.Success);
    }

    [SkippableFact]
    public async Task Publishing_sets_the_upstream_and_lands_the_commit()
    {
        Setup();
        Git(Work, $"remote add origin \"{Remote}\"");

        var result = await Client.PublishBranchAsync(
            "origin", "main", null, CancellationToken.None);

        Assert.True(result.Success);

        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.Equal("origin/main", status.Branch.Upstream);
        Assert.Equal(0, status.Branch.Ahead);
        Assert.Equal(0, status.Branch.Behind);
    }

    [SkippableFact]
    public async Task A_plain_push_works_after_publishing()
    {
        // The point of setting an upstream: everything afterwards is an ordinary push.
        Setup();
        Git(Work, $"remote add origin \"{Remote}\"");
        await Client.PublishBranchAsync("origin", "main", null, CancellationToken.None);

        File.AppendAllText(Path.Combine(Work, "a.txt"), "second\n");
        Git(Work, "add .");
        Git(Work, "commit -qm second");

        var result = await Client.PushAsync(null, CancellationToken.None);

        Assert.True(result.Success);
    }

    [SkippableFact]
    public async Task Publishing_to_a_remote_that_is_not_there_fails_with_a_reason()
    {
        Setup();
        Git(Work, "remote add origin \"C:/definitely/not/a/repository\"");

        var result = await Client.PublishBranchAsync(
            "origin", "main", null, CancellationToken.None);

        Assert.False(result.Success);

        // The notification puts this in front of the user verbatim, so it has to be a
        // sentence rather than an exit code.
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [SkippableFact]
    public async Task Ahead_and_behind_are_reported_once_an_upstream_exists()
    {
        Setup();
        Git(Work, $"remote add origin \"{Remote}\"");
        await Client.PublishBranchAsync("origin", "main", null, CancellationToken.None);

        File.AppendAllText(Path.Combine(Work, "a.txt"), "local\n");
        Git(Work, "add .");
        Git(Work, "commit -qm local");

        var status = await Client.StatusAsync(CancellationToken.None);

        Assert.Equal(1, status.Branch.Ahead);
        Assert.Equal(0, status.Branch.Behind);
    }
}
