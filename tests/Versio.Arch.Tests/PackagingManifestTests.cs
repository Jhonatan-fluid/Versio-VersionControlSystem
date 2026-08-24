using System.IO;
using System.Text.Json;
using Versio.TestSupport;
using Xunit;

namespace Versio.Arch.Tests;

/// <summary>
/// The pack pins, read from disk. A missing hash is a slower download, not a pin.
/// </summary>
public sealed class PackagingManifestTests
{
    [Fact]
    public void PortableGit_is_pinned_by_url_and_sha256()
    {
        var path = Path.Combine(TestPaths.Root, "third_party", "manifest.json");
        Assert.True(File.Exists(path), "third_party/manifest.json is the pack pin");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var git = doc.RootElement.GetProperty("dependencies").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "PortableGit");

        var sha = git.GetProperty("sha256").GetString();
        var url = git.GetProperty("url").GetString();
        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.Equal(64, sha!.Length);
        Assert.Matches("^[0-9a-f]{64}$", sha);
        Assert.Contains("PortableGit", url, StringComparison.Ordinal);
        Assert.DoesNotContain("MinGit", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Vpk_version_is_pinned()
    {
        var path = Path.Combine(TestPaths.Root, "third_party", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var vpk = doc.RootElement.GetProperty("dependencies").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "vpk");

        Assert.Equal("1.2.0", vpk.GetProperty("version").GetString());
    }

    [Fact]
    public void Notices_file_names_git_and_codescribe()
    {
        var path = Path.Combine(TestPaths.Root, "third_party", "THIRD-PARTY-NOTICES.txt");
        var text = File.ReadAllText(path);
        Assert.Contains("GPL-2.0", text, StringComparison.Ordinal);
        Assert.Contains("Apache-2.0", text, StringComparison.Ordinal);
        Assert.Contains("git-for-windows", text, StringComparison.Ordinal);
        Assert.Contains("codescribe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_entry_point_is_at_the_repo_root()
    {
        Assert.True(File.Exists(Path.Combine(TestPaths.Root, "Build_Package.bat")));
        Assert.True(File.Exists(Path.Combine(TestPaths.Root, "tools", "pack", "Pack-Versio.ps1")));
        Assert.True(File.Exists(Path.Combine(TestPaths.Root, "tools", "pack", "Fetch-Deps.ps1")));
    }

    [Fact]
    public void Pack_overwrites_the_same_version_without_a_prompt()
    {
        // --yes alone still draws the y/n prompt and waits for a timeout.
        // --legacyConsole turns the question off; together they overwrite.
        // Global flags must sit before `pack`.
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Pack-Versio.ps1"));
        Assert.Contains("--legacyConsole --yes --skip-updates pack", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_pins_GCM_on_the_bundled_git()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Pack-Versio.ps1"));
        Assert.Contains("config --system credential.helper manager", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Git_spawn_applies_bundled_defaults_in_one_place()
    {
        // ApplyBundledDefaults is PATH + the GCM pin. Calling only PrependHelperPath
        // again would bring CredentialHelperSelector back on every fill.
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Versio.Infrastructure", "Git", "GitCliClient.cs"));
        Assert.Contains("GitExecutable.ApplyBundledDefaults", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GitExecutable.PrependHelperPath(psi", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fetch_deps_skips_a_cached_PortableGit_and_a_pinned_vpk()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Fetch-Deps.ps1"));
        Assert.Contains("PortableGit cache ok", text, StringComparison.Ordinal);
        Assert.Contains("already in tools\\pack\\vpk", text, StringComparison.Ordinal);
    }
}
