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
    public void The_Velopack_package_and_the_vpk_tool_are_the_same_version()
    {
        // The csproj comment demands this and nothing enforced it: the Velopack package is
        // the in-app half (install hooks, Update.exe protocol) and vpk is the packer.
        // Bumping one without the other ships an installer whose hooks and packer disagree,
        // which is drift of exactly the kind the manifest exists to prevent.
        var manifest = Path.Combine(TestPaths.Root, "third_party", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var pinned = doc.RootElement.GetProperty("dependencies").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "vpk")
            .GetProperty("version").GetString();

        var csproj = File.ReadAllText(
            Path.Combine(TestPaths.Root, "src", "Versio.App", "Versio.App.csproj"));

        Assert.Contains(
            $"<PackageReference Include=\"Velopack\" Version=\"{pinned}\" />",
            csproj,
            StringComparison.Ordinal);
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
        Assert.Contains("already in third_party\\cache\\vpk", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_asks_how_to_bump_VersioVersion()
    {
        // The menu and the writer moved into PackCommon.ps1 so the publish script asks the
        // same question the same way. The pack still takes -Version and dot-sources them; a
        // pack that stopped loading the common file would fail on its first Log call, but
        // only when somebody ran it, so the line is pinned here.
        var pack = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Pack-Versio.ps1"));
        var common = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "PackCommon.ps1"));
        Assert.Contains(". (Join-Path $PSScriptRoot 'PackCommon.ps1')", pack, StringComparison.Ordinal);
        Assert.Contains("[string] $Version", pack, StringComparison.Ordinal);
        Assert.Contains("Last number (patch)", common, StringComparison.Ordinal);
        Assert.Contains("Middle (minor)", common, StringComparison.Ordinal);
        Assert.Contains("First (major)", common, StringComparison.Ordinal);
        Assert.Contains("Keep", common, StringComparison.Ordinal);
        Assert.Contains("Type one", common, StringComparison.Ordinal);
        Assert.Contains("function Set-VersioVersion", common, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::WriteAllText", common, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_installs_per_user_so_an_update_needs_no_administrator()
    {
        // PerMachine puts Versio under Program Files, and every update then needs elevation:
        // a UAC prompt per release, and a failure outright for a user who is not an
        // administrator - which, on a managed laptop, is most of them.
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Pack-Versio.ps1"));
        Assert.Contains("--instLocation PerUser", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PerMachine --", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_entry_point_is_at_the_repo_root()
    {
        Assert.True(File.Exists(Path.Combine(TestPaths.Root, "Publish_Release.bat")));
        Assert.True(File.Exists(Path.Combine(TestPaths.Root, "tools", "pack", "Publish-Versio.ps1")));
    }

    [Fact]
    public void Publish_never_puts_the_token_on_a_command_line()
    {
        // Every child-process command line is written to publish.log by Invoke-LoggedProcess,
        // and is readable by any process on the machine while vpk runs. The token reaches vpk
        // through a response file instead; a format string that spelled out --token would put
        // it straight back in the log.
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Publish-Versio.ps1"));
        Assert.Contains("New-TokenResponseFile", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--token {", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--token $", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_update_repository_is_named_once()
    {
        // The app checks it and the publish uploads to it. Two copies of the URL would let a
        // build look in one repository while releases went to another, and every installed
        // copy would report itself up to date for ever.
        var props = File.ReadAllText(Path.Combine(TestPaths.Root, "Directory.Build.props"));
        var app = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Versio.App", "Versio.App.csproj"));
        var publish = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Publish-Versio.ps1"));
        Assert.Contains("<VersioUpdateRepo>https://github.com/", props, StringComparison.Ordinal);
        Assert.Contains("$(VersioUpdateRepo)", app, StringComparison.Ordinal);
        Assert.Contains("<VersioUpdateRepo>", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com/fluidstackio", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void The_update_flow_is_wired_where_no_unit_test_reaches()
    {
        // The view model's half is driven by UpdatesViewModelTests. These four lines are the
        // composition root's and the window's, and deleting any one leaves every test green:
        // no launch check, an update downloaded and never installed, a restart that never
        // closes the window, or a refused close that goes on restarting at the next exit.
        var startup = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Versio.App", "App.xaml.cs"));
        var window = File.ReadAllText(
            Path.Combine(TestPaths.Root, "src", "Versio.App", "Views", "MainWindow.xaml.cs"));
        Assert.Contains("shell.Updates.CheckOnLaunchAsync(", startup, StringComparison.Ordinal);
        Assert.Contains("shell.Updates.ApplyOnExit()", startup, StringComparison.Ordinal);
        Assert.Contains("shell.Updates.RestartRequested +=", window, StringComparison.Ordinal);
        Assert.Contains("_shell.Updates.RestartDeclined()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_and_publish_scripts_stay_pure_ascii()
    {
        // Windows PowerShell 5.1 reads a BOM-less script as the ANSI codepage. One em-dash in
        // a string and the console shows mojibake - or a comparison quietly stops matching.
        foreach (var name in new[] { "Pack-Versio.ps1", "PackCommon.ps1", "Publish-Versio.ps1" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(TestPaths.Root, "tools", "pack", name));
            var offender = Array.FindIndex(bytes, b => b > 0x7F);
            Assert.True(offender < 0, $"{name} has a non-ASCII byte at offset {offender}");
        }
    }

    [Fact]
    public void VersioVersion_is_the_single_source_and_has_no_git_suffix()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "Directory.Build.props"));
        Assert.Contains("<VersioVersion>", text, StringComparison.Ordinal);
        Assert.Contains(
            "<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void System_information_uses_the_informational_product_version()
    {
        var shell = File.ReadAllText(
            Path.Combine(TestPaths.Root, "src", "Versio.App", "ViewModels", "ShellViewModel.cs"));
        var startup = File.ReadAllText(
            Path.Combine(TestPaths.Root, "src", "Versio.App", "App.xaml.cs"));
        Assert.Contains("new(\"Version\", AppVersion.Display)", shell, StringComparison.Ordinal);
        Assert.Contains("Versio {AppVersion.Display} starting", startup, StringComparison.Ordinal);
    }
}
