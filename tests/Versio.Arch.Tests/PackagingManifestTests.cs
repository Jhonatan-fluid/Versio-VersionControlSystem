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
    public void Pack_trims_PortableGit_before_the_gates_that_prove_it_still_works()
    {
        // The trim has to run after the extraction-finished poll and before the LFS, GCM, sh
        // and helper-pin checks: moved below them, those gates would pass on a tree that is
        // not the one that ships. Deleting the call leaves every other test green and ships
        // 140 MB more in every install and every update.
        var pack = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Pack-Versio.ps1"));
        Assert.Contains(". (Join-Path $PSScriptRoot 'Trim-PortableGit.ps1')", pack, StringComparison.Ordinal);

        var poll = pack.IndexOf("Log (\"GCM at \"", StringComparison.Ordinal);
        var trim = pack.IndexOf("Remove-UnusedPortableGit $gitOut", StringComparison.Ordinal);
        var lfs = pack.IndexOf("& $gitExe 'lfs' 'version'", StringComparison.Ordinal);
        var gcm = pack.IndexOf("& $gitExe 'credential-manager' '--version'", StringComparison.Ordinal);
        var sh = pack.IndexOf("alias.versio-sh=!echo sh-ok", StringComparison.Ordinal);
        var pin = pack.IndexOf("config --system credential.helper manager", StringComparison.Ordinal);
        Assert.All(new[] { poll, trim, lfs, gcm, sh, pin }, i => Assert.True(i >= 0));
        Assert.True(poll < trim, "the trim must wait for the extraction-finished poll");
        Assert.True(trim < lfs && trim < gcm && trim < sh && trim < pin,
            "every payload gate must run against the trimmed git");
    }

    [Fact]
    public void PortableGit_denylist_never_names_what_Versio_runs()
    {
        // A denylist fails safe only while it stays a list of whole unused subsystems. Each
        // fragment here is something a Versio git spawn reaches: GCM (MinGit was rejected for
        // lacking it, FINDINGS 2026-08-24), LFS, the remote helper and its TLS stack, the MSYS
        // runtime and sh that every hook runs under, ssh, gpg for signed commits, astextplain's
        // tools, and the GPL text that has to travel with the binaries.
        var entries = DenylistEntries();
        string[] kept =
        [
            "credential", "gcm", "avalonia", "identity", "skia", "harfbuzz", "msal",
            "git-lfs", "git-remote", "curl", "ssl", "crypto", "msys-2.0", "\\sh.exe", "\\bash.exe",
            "coreutils", "ssh", "gpg", "gnupg", "astextplain", "file.exe", "misc", "antiword",
            "pdftotext", "license", "copying", "\\cmd", "etc\\", "git.exe", "pki",
        ];
        foreach (var entry in entries)
        {
            foreach (var fragment in kept)
            {
                Assert.False(
                    entry.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"denylist entry '{entry}' names '{fragment}', which Versio needs");
            }

            // A whole bin or libexec folder, or a bare wildcard, is an allowlist's job.
            var segments = entry.Split('\\');
            Assert.DoesNotContain("*", segments);
            Assert.True(segments.Length >= 2 || entry.EndsWith(".exe", StringComparison.Ordinal),
                $"denylist entry '{entry}' is a top-level folder");
        }

        // And it still cuts what was measured: an emptied list would pass every check above.
        Assert.Contains(@"usr\share\vim", entries);
        Assert.Contains(@"usr\lib\perl5", entries);
        Assert.Contains(@"usr\share\perl5", entries);
        Assert.Contains(@"mingw64\share\doc\git-doc", entries);
        Assert.Contains(@"mingw64\lib\tcl8.6", entries);
    }

    [Fact]
    public void Dropping_perl_marks_Word_2007_diffs_binary()
    {
        // astextplain extracts .docx text with docx2txt.pl. Without perl it falls back to
        // `cat` and the diff panel shows zip bytes as text; -diff makes it "Binary files
        // differ" instead. Removing perl from the list is the only reason to remove this.
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Trim-PortableGit.ps1"));
        Assert.Contains("Set-WordDiffBinary $Root $Write", text, StringComparison.Ordinal);
        Assert.Contains("docx|docm|dotx|dotm", text, StringComparison.Ordinal);
        Assert.Contains("'$1$2-diff'", text, StringComparison.Ordinal);
    }

    private static List<string> DenylistEntries()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "tools", "pack", "Trim-PortableGit.ps1"));
        var start = text.IndexOf("$script:PortableGitDenylist = @(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Trim-PortableGit.ps1 declares $script:PortableGitDenylist");
        var end = text.IndexOf("\n)", start, StringComparison.Ordinal);
        Assert.True(end > start);

        // Single-quoted literals only, read line by line so a quote inside a comment is not
        // taken for an entry.
        var entries = new List<string>();
        foreach (var line in text[start..end].Split('\n').Skip(1))
        {
            var code = line.TrimStart();
            if (code.StartsWith('#'))
            {
                continue;
            }

            entries.AddRange(System.Text.RegularExpressions.Regex.Matches(code, "'([^']+)'")
                .Select(m => m.Groups[1].Value));
        }

        Assert.NotEmpty(entries);
        return entries;
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

        // The overlay's keyboard half. UpdateOverlayWindowTests sees the overlay appear, but
        // a never-presented window receives no keys, so the handler that drops keys aimed
        // behind it and the move of focus onto it are only reachable by reading the source.
        Assert.Contains("UpdateOverlay.IsAncestorOf(target)", window, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(FocusUpdateOverlay", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_and_publish_scripts_stay_pure_ascii()
    {
        // Windows PowerShell 5.1 reads a BOM-less script as the ANSI codepage. One em-dash in
        // a string and the console shows mojibake - or a comparison quietly stops matching.
        foreach (var name in new[] { "Pack-Versio.ps1", "PackCommon.ps1", "Publish-Versio.ps1", "Trim-PortableGit.ps1" })
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
