using Versio.Core.Git;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The large-file preflight reads a <c>.gitattributes</c> rule the way git itself does.
/// </summary>
/// <remarks>
/// Each row writes the file into a throwaway repository, asks <c>git check-attr filter</c>
/// about the path, and checks that the preflight's "protected" is git's "lfs". The
/// expectation is computed from git at test time, not copied from a transcript, so a row
/// cannot pin a guess. The rows are the shapes the 2026-10-25 review's item 5 found read
/// wrongly — a bare <c>filter</c>, a character class — and the ones the fix had to get
/// right beside them: a <c>**</c> that is not between slashes, a slash inside a class, a
/// class that never closes, a glob escape, a POSIX class.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LargeFilePreflightGitAgreementTests : IDisposable
{
    private const long Mb = 1024 * 1024;

    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "Versio", "attrs", Guid.NewGuid().ToString("N")[..8]);

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

    [SkippableTheory]
    [InlineData("*.bin filter=lfs\nhuge.bin filter\n", "huge.bin")]
    [InlineData("*.bin filter=lfs\nhuge.bin filter\n", "other.bin")]
    [InlineData("*.bin filter=lfs\n*.bin filter=lfs filter\n", "huge.bin")]
    [InlineData("*.bin filter=lfs\nhuge[.]bin -filter\n", "huge.bin")]
    [InlineData("*.bin filter=lfs\nhuge[.]bin -filter\n", "other.bin")]
    [InlineData("*.bin filter=lfs\nhuge[.bin -filter\n", "huge.bin")]
    [InlineData("v[!0-9].bin filter=lfs\n", "va.bin")]
    [InlineData("v[!0-9].bin filter=lfs\n", "v1.bin")]
    [InlineData("v[]].bin filter=lfs\n", "v].bin")]
    [InlineData("[[:digit:]].bin filter=lfs\n", "1.bin")]
    [InlineData("a[/]b.bin filter=lfs\n", "a/b.bin")]
    [InlineData("a**b.bin filter=lfs\n", "a/x/b.bin")]
    [InlineData("a**b.bin filter=lfs\n", "axb.bin")]
    [InlineData("**a/huge.bin filter=lfs\n", "x/a/huge.bin")]
    [InlineData("a/**/huge.bin filter=lfs\n", "a/x/y/huge.bin")]
    [InlineData("huge[.bin filter=lfs\n", "huge[.bin")]
    [InlineData("huge\\*.bin filter=lfs\n", "huge*.bin")]
    [InlineData("huge\\*.bin filter=lfs\n", "hugeX.bin")]
    [InlineData("/huge.bin filter=lfs\n", "a/huge.bin")]
    // The 2026-10-25 review's item 6: git's C-quoting, octal escapes included, and the
    // short or unknown escapes that make git drop the line.
    [InlineData("*.bin filter=lfs\n\"huge\\056bin\" -filter\n", "huge.bin")]
    [InlineData("*.bin filter=lfs\n\"huge\\056bin\" -filter\n", "other.bin")]
    [InlineData("*.bin filter=lfs\n\"huge\\56bin\" -filter\n", "huge.bin")]
    [InlineData("*.bin filter=lfs\n\"huge\\qbin\" -filter\n", "huge.bin")]
    [InlineData("\"hug\\145.bin\" filter=lfs\n", "huge.bin")]
    [InlineData("\"\\303\\244.bin\" filter=lfs\n", "ä.bin")]
    // The third round's item 2: a range written backwards keeps its low end as a member.
    [InlineData("v[z-a].bin filter=lfs\n", "vz.bin")]
    [InlineData("v[z-a].bin filter=lfs\n", "va.bin")]
    [InlineData("v[!z-a].bin filter=lfs\n", "vz.bin")]
    [InlineData("v[!z-a].bin filter=lfs\n", "va.bin")]
    public async Task The_preflight_agrees_with_check_attr(string gitattributes, string path)
    {
        Skip.IfNot(GitAvailable, "git not on PATH");
        Directory.CreateDirectory(_repo);
        await RunAsync("init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(_repo, ".gitattributes"), gitattributes);

        var (exit, answer) = await RunAsync("check-attr", "filter", "--", path);

        Assert.Equal(0, exit);
        var gitSaysLfs = answer.TrimEnd().EndsWith(": filter: lfs", StringComparison.Ordinal);
        var versioSaysProtected = LargeFilePreflight.Unprotected([(path, 20 * Mb)], gitattributes).Count == 0;

        Assert.True(gitSaysLfs == versioSaysProtected,
            $"git: `{answer.Trim()}`; preflight: {(versioSaysProtected ? "protected" : "unprotected")}");
    }

    private async Task<(int ExitCode, string StdOut)> RunAsync(params string[] args)
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
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout);
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
