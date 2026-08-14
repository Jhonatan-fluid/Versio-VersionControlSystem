using Versio.Core.Git;
using Versio.Infrastructure.Git;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// Reading a forge's hidden request refs, against real git.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bare repository can hold any ref a forge can.</b> GitHub publishes pull-request heads
/// under <c>refs/pull/N/head</c> and GitLab under <c>refs/merge-requests/N/head</c>, and those
/// are ordinary refs — so writing them into a local bare remote reproduces exactly what
/// <c>ls-remote</c> sees against the real thing, with no network, credentials or account.
/// </para>
/// <para>
/// <b>This is the half the unit tests cannot reach.</b> <c>ForgeTests</c> parses
/// <c>ls-remote</c> output this test file wrote by hand; here git produces it. The two failures
/// that only show up this way are a pattern git rejects or expands differently from expectation,
/// and <c>--heads</c> silently filtering the whole namespace away — which is exactly why this
/// could not reuse <c>LsRemoteAsync</c>.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RequestRefProbeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Versio", "prrefs", Guid.NewGuid().ToString("N")[..8]);

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
    }

    [SkippableFact]
    public async Task A_request_ref_pointing_at_this_branch_is_found()
    {
        Setup();

        var tip = (await Client.RevParseAsync("main", CancellationToken.None)).Trim();
        Assert.NotEqual(string.Empty, tip);

        // Exactly what a forge publishes when somebody opens a pull request from this commit.
        Git(Remote, $"update-ref refs/pull/7/head {tip}");

        var refs = await Client.LsRemoteRefsAsync(
            "origin", Forge.RequestRefPattern(ForgeKind.GitHub), CancellationToken.None);

        Assert.Contains("refs/pull/7/head", refs);
        Assert.Equal(7, Forge.RequestNumber(refs, tip));
    }

    [SkippableFact]
    public async Task GitLabs_namespace_is_read_by_the_same_call()
    {
        Setup();

        var tip = (await Client.RevParseAsync("main", CancellationToken.None)).Trim();
        Git(Remote, $"update-ref refs/merge-requests/45/head {tip}");

        var refs = await Client.LsRemoteRefsAsync(
            "origin", Forge.RequestRefPattern(ForgeKind.GitLab), CancellationToken.None);

        Assert.Equal(45, Forge.RequestNumber(refs, tip));
    }

    [SkippableFact]
    public async Task A_remote_with_no_request_refs_answers_empty_rather_than_failing()
    {
        // The overwhelmingly common case: a branch with no request open. It has to be
        // indistinguishable from success-with-no-rows, or the poller would count it a failure
        // and back off towards silence.
        Setup();

        var refs = await Client.LsRemoteRefsAsync(
            "origin", Forge.RequestRefPattern(ForgeKind.GitHub), CancellationToken.None);

        Assert.Equal(string.Empty, refs.Trim());
        Assert.Null(Forge.RequestNumber(refs, "whatever"));
    }

    [SkippableFact]
    public async Task The_branch_probe_would_not_have_found_these()
    {
        // WHY THIS METHOD EXISTS AT ALL. LsRemoteAsync passes --heads and builds
        // refs/heads/<name> itself, which is right for the poller and cannot see this
        // namespace. Widening that one instead would have let it match a tag.
        Setup();

        var tip = (await Client.RevParseAsync("main", CancellationToken.None)).Trim();
        Git(Remote, $"update-ref refs/pull/7/head {tip}");

        Assert.Equal(
            string.Empty,
            (await Client.LsRemoteAsync("origin", "pull/7/head", CancellationToken.None)).Trim());
    }

    [SkippableFact]
    public async Task An_empty_pattern_asks_the_remote_nothing()
    {
        // Azure and Bitbucket publish no such refs, so RequestRefPattern returns empty for
        // them and this must not spawn a process to learn nothing.
        Setup();

        Assert.Equal(
            string.Empty,
            await Client.LsRemoteRefsAsync("origin", string.Empty, CancellationToken.None));
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
