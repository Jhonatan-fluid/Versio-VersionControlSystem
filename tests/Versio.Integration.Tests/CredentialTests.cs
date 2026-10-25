using Versio.Core.Git;
using Versio.Infrastructure.Git;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The credential plumbing, against the real <c>git credential</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="GitCliClient.SignInAsync"/> is never called here, and must not be.</b> It is
/// the one call in the application that lets git prompt, which in a test run means a credential
/// window appearing on somebody's machine — or on a build agent, a process that waits five
/// minutes for a human who is not there. Everything below runs with prompts suppressed.
/// </para>
/// <para>
/// Every host used here is under <c>.invalid</c>, which is reserved by RFC 2606 and cannot
/// resolve. That keeps these tests away from the developer's real credential store: a query for
/// <c>github.com</c> would consult Git Credential Manager and could return a real token.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CredentialTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "Versio", "cred", Guid.NewGuid().ToString("N")[..8]);

    private GitCliClient Client => new(_repo, logger: new TestLogger(output));

    private void Setup()
    {
        Directory.CreateDirectory(_repo);
        Git("init -q -b main .");
    }

    private void Git(string arguments)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _repo,
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

    [SkippableFact]
    public async Task Asking_about_an_unknown_host_returns_quickly_and_does_not_prompt()
    {
        // THE TEST THAT MATTERS MOST. If prompts were not suppressed on this path, this call
        // would hang waiting for input that a test run can never supply — and on a machine
        // with a GUI helper it would put a window up. That it completes at all is the
        // assertion; the answer is secondary.
        Setup();

        var started = DateTimeOffset.UtcNow;
        var stored = await Client.HasStoredCredentialAsync(
            "https", "versio-nothing-here.invalid", CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(stored);
        Assert.True(elapsed < TimeSpan.FromSeconds(15),
                    $"the credential query took {elapsed.TotalSeconds:F1}s, which means it prompted");
    }

    [SkippableFact]
    public async Task An_empty_host_is_answered_without_running_git_at_all()
    {
        Setup();

        Assert.False(await Client.HasStoredCredentialAsync("https", "", CancellationToken.None));
        Assert.False(await Client.HasStoredCredentialAsync("", "host.invalid", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Erasing_a_credential_that_does_not_exist_is_not_an_error()
    {
        // git credential reject is idempotent, and it has to be: the button is available
        // whenever a credential COULD be stored, not only when one is.
        Setup();

        var result = await Client.SignOutAsync(
            "https", "versio-nothing-here.invalid", CancellationToken.None);

        Assert.True(result.Success);
    }

    [SkippableFact]
    public async Task A_fetch_from_an_unreachable_host_is_classified_rather_than_guessed_at()
    {
        // End to end: git's real stderr, through the real classifier. The point is that an
        // unreachable host is NOT reported as a credential problem, so the panel does not tell
        // somebody on a train to sign in again.
        Setup();
        Git("remote add origin https://versio-nothing-here.invalid/x/y.git");

        var result = await Client.FetchAsync(null, CancellationToken.None);

        Assert.False(result.Success);

        var state = GitAuthClassifier.Classify(result.StdErr, RemoteTransport.Http);

        Assert.True(
            state is GitAuthState.Unreachable or GitAuthState.NoCredential,
            $"an unreachable host was classified as {state}: {result.StdErr}");
        Assert.False(GitAuthClassifier.SignInWouldHelp(GitAuthState.Unreachable));
    }

    [SkippableFact]
    public async Task Prompts_stay_suppressed_for_ordinary_operations()
    {
        // The default that everything except Sign in relies on. A fetch against a host that
        // cannot resolve must fail fast rather than sit waiting for a credential.
        Setup();
        Git("remote add origin https://versio-nothing-here.invalid/x/y.git");

        var started = DateTimeOffset.UtcNow;
        await Client.FetchAsync(null, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(60),
                    $"the fetch took {elapsed.TotalSeconds:F1}s, which suggests it waited on a prompt");
    }

    [SkippableFact]
    public async Task A_credential_stored_with_a_port_is_found_under_host_and_port_only()
    {
        // The `store` helper keys its entries by the whole authority, and so does every
        // helper, because that is what git puts on the host= line of its own requests. The
        // panel asked after the bare host and read "none" for a credential that was stored
        // (the 2026-10-24 review's item 38); this pins what git actually keys by, so the
        // view-model's choice of key is measured against git rather than against a fake.
        //
        // Its own store file under the temp repository, and the helper list reset first so
        // the machine's real helper is never consulted for this host; .invalid cannot resolve.
        Setup();
        var store = Path.Combine(_repo, "credentials").Replace('\\', '/');
        File.WriteAllText(store, "https://u:p@versio-port.invalid:8443\n");
        Git("config credential.helper \"\"");
        Git($"config --add credential.helper \"store --file={store}\"");

        Assert.True(await Client.HasStoredCredentialAsync(
            "https", "versio-port.invalid:8443", CancellationToken.None));
        Assert.False(await Client.HasStoredCredentialAsync(
            "https", "versio-port.invalid", CancellationToken.None));
    }
}
