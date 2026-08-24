using Versio.Core.Git;
using Versio.Infrastructure.Git;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The life and death of an upstream, against real git: a branch deleted on the remote —
/// a forge cleaning up after a merge, which is the ordinary end of every pull request.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the chain FINDINGS 2026-08-23 is about</b>, and each link is proved here with
/// git producing the bytes rather than a fixture imitating them: the ordinary fetch prunes
/// the stale tracking ref, porcelain then states the loss by omission (an upstream and no
/// ahead/behind line), the parser flags the omission, and the standing names it — instead of
/// the "Up to date with origin/x" the panel used to report over a branch nobody else could
/// even see.
/// </para>
/// <para>
/// Deleting the ref directly in the bare remote, not via <c>push --delete</c> from the work
/// clone: the defect is specifically about a deletion this repository did not perform and so
/// was never told about.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DeletedUpstreamTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Versio", "goneup", Guid.NewGuid().ToString("N")[..8]);

    private string Work => Path.Combine(_root, "work");

    private string Remote => Path.Combine(_root, "remote.git");

    private GitCliClient Client => new(Work, logger: new TestLogger(output));

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
        Git(Work, $"remote add origin \"{Remote}\"");
        Git(Work, "push -q --set-upstream origin main");
        Git(Work, "checkout -q -b feature");
        Git(Work, "push -q --set-upstream origin feature");
    }

    [SkippableFact]
    public async Task A_branch_deleted_on_the_remote_is_pruned_and_its_standing_says_gone()
    {
        Setup();

        // The forge merges the request and deletes the branch. Nothing told this clone.
        Git(Remote, "update-ref -d refs/heads/feature");

        // BEFORE the fetch the stale tracking ref is still here, porcelain still counts
        // against it, and the standing reads InSync - the lie this whole chain exists to
        // end. Asserted so that if git ever starts reporting the loss unprompted, this test
        // says the pre-fetch half of the reasoning is obsolete.
        var before = await Client.StatusAsync(CancellationToken.None);
        Assert.False(before.Branch.UpstreamGone);
        Assert.Equal(TrackingState.InSync, RemoteStanding.For(before.Branch, true).State);

        var fetch = await Client.FetchAsync(null, CancellationToken.None);
        Assert.True(fetch.Success);

        // The ordinary fetch - the button, the poller's catch-up - prunes the ghost...
        Assert.Equal(
            string.Empty,
            (await Client.RevParseAsync("origin/feature", CancellationToken.None)).Trim());

        // ...and the loss now flows through porcelain's omission into a named state.
        var after = await Client.StatusAsync(CancellationToken.None);
        Assert.True(after.Branch.UpstreamGone);
        Assert.Equal("origin/feature", after.Branch.Upstream);

        var standing = RemoteStanding.For(after.Branch, hasRemote: true);
        Assert.Equal(TrackingState.UpstreamGone, standing.State);
        Assert.True(standing.CanPush);
        Assert.False(standing.CanPull);
    }

    [SkippableFact]
    public async Task A_healthy_upstream_survives_the_pruning_fetch()
    {
        // The other half of adding --prune to every fetch: it must only drop refs whose
        // branches are gone. A prune that touched a living upstream would turn every fetch
        // into the defect it fixes.
        Setup();

        var fetch = await Client.FetchAsync(null, CancellationToken.None);
        Assert.True(fetch.Success);

        var status = await Client.StatusAsync(CancellationToken.None);
        Assert.False(status.Branch.UpstreamGone);
        Assert.Equal(TrackingState.InSync, RemoteStanding.For(status.Branch, true).State);
        Assert.NotEqual(
            string.Empty,
            (await Client.RevParseAsync("origin/feature", CancellationToken.None)).Trim());
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
            // A temp directory that outlives the run is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
            // git marks its objects read-only, and that surfaces as this rather than IOException.
        }
    }
}
