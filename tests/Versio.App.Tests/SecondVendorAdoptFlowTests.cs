using System.Diagnostics;
using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The session follows the path's vendor, not the first registered format.
/// </summary>
/// <remarks>
/// Proven by mutation: restoring the <c>PlcFormats.All[0]</c> pin in
/// <c>SyncViewModel</c> makes this test refuse the <c>.ap21</c> as "not a CODESYS project"
/// and write <c>.~u</c> rules into <c>.gitignore</c>. That is the whole defect.
/// </remarks>
public sealed class SecondVendorAdoptFlowTests
{
    [SkippableFact]
    public async Task An_ap21_is_adopted_as_tia_portal_and_gitignore_gets_its_transients()
    {
        Skip.IfNot(GitIsAvailable(), "git is not on PATH");

        using var repo = new TempRepo();
        var plc = Path.Combine(repo.Path, "plc");
        Directory.CreateDirectory(plc);
        File.WriteAllText(Path.Combine(plc, "Station.ap21"), "pointer");
        File.WriteAllText(Path.Combine(plc, "Archive.al21"), "archive");
        Directory.CreateDirectory(Path.Combine(plc, "System"));
        File.WriteAllText(Path.Combine(plc, "System", "PEData.plf"), "program");
        Directory.CreateDirectory(Path.Combine(plc, "Logs"));
        File.WriteAllText(Path.Combine(plc, "Logs", "run.log"), "noise");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        var tracked = Assert.Single(shell.Sync.Projects, p => p.Tracked);
        Assert.Equal("plc/Station.ap21", tracked.Path);
        Assert.DoesNotContain(shell.Sync.Projects, p => p.Path.Contains("Archive.al21"));

        var config = await File.ReadAllTextAsync(Path.Combine(repo.Path, ".versio", "repo.json"));
        Assert.Contains("\"vendor\": \"TiaPortal\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("\"vendor\": \"Codesys\"", config, StringComparison.Ordinal);

        var ignore = await File.ReadAllTextAsync(Path.Combine(repo.Path, ".gitignore"));
        Assert.Contains("plc/Logs/", ignore, StringComparison.Ordinal);
        Assert.Contains("Station.info", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain(".~u", ignore, StringComparison.Ordinal);

        shell.Sync.NoteSelection("plc/Logs/run.log");
        Assert.Null(shell.Sync.SelectedProject);

        shell.Sync.NoteSelection("plc/System/PEData.plf");
        Assert.NotNull(shell.Sync.SelectedProject);
        Assert.Equal("plc/Station.ap21", shell.Sync.SelectedProject!.Path);
    }

    private static bool GitIsAvailable()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            git!.WaitForExit();
            return git.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
