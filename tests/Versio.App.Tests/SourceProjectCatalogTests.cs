using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A File-Based Storage project's library manager, tasks, devices, text list and project settings
/// drawn as the tables the classic mirror's objects get, from the real fixture.
/// </summary>
/// <remarks>
/// The owner, 2026-10-10: "libraries is still not rendering, device is not rendering, task
/// configuration is not rendering, global text list, project settings". Each drew as raw source,
/// because the views read only Versio's YAML and an FBS folder has none.
/// </remarks>
public sealed class SourceProjectCatalogTests
{
    private const string App = "Device/Plc Logic/Application";

    private static void CopyFixture(string to)
    {
        var from = Path.Combine(FixtureRoot(), "fixtures", "projects", "RefrigeratorControl_FBS", "RefrigeratorControl.fbsproj");
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (relative.StartsWith(".sidecars", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.Combine(to, "plc", "RefrigeratorControl.fbsproj", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Versio.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static TreeRow Row(IEnumerable<TreeRow> rows, string path)
    {
        var parts = path.Split('/');
        var current = rows.ToList();
        TreeRow? found = null;
        foreach (var part in parts)
        {
            found = Assert.Single(current, r => r.Node.Name == part);
            current = found.Children.ToList();
        }

        return found!;
    }

    private static async Task<ShellViewModel> OpenWithOneLibraryChangedAsync(TempRepo repo)
    {
        CopyFixture(repo.Path);
        repo.Git("-c core.autocrlf=false -c core.longpaths=true add -A");
        repo.Git("commit -q -m fbs");

        var libraries = Path.Combine(repo.Path, "plc", "RefrigeratorControl.fbsproj", "Device.device.xml.v3^",
            "Plc Logic.plclogic.xml.v3^", "Application.application.xml.v3^", "Libraries.json");
        File.WriteAllText(libraries, File.ReadAllText(libraries).Replace(
            "\"Util\": \"Util, 3.5.22.0 (System)\"", "\"Util\": \"Util, 3.5.21.0 (System)\"", StringComparison.Ordinal));

        var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        await shell.RefreshAsync();
        return shell;
    }

    [Fact]
    public async Task Each_kind_of_object_draws_as_its_table()
    {
        using var repo = new TempRepo(identity: true);
        using var shell = await OpenWithOneLibraryChangedAsync(repo);
        var project = Assert.Single(shell.Sync.Tree, r => r.Node.Kind == ProjectNodeKind.Project).Children;

        shell.Sync.SelectedNode = Row(project, App + "/Libraries");
        var libraries = Assert.IsType<LibraryCatalog>(shell.RenderedContent);
        Assert.Contains(libraries.References, r => r.Name == "#Util" && r.Redirection == "Util, 3.5.21.0 (System)");

        // The committed copy is read by the same format, so the one change is marked.
        await Eventually.Until(() => shell.RenderedContent is LibraryCatalog { HasChangeSummary: true }, "the library table is marked");
        Assert.Contains(((LibraryCatalog)shell.RenderedContent!).References, r => r.Name == "#Util" && r.Mark != RowMark.None);

        shell.Sync.SelectedNode = Row(project, App + "/Task Configuration");
        Assert.Equal(["EtherCAT_Task", "MainTask", "VISU_TASK"],
            Assert.IsType<TaskCatalog>(shell.RenderedContent).Tasks.Select(t => t.Name).Order(StringComparer.Ordinal));

        shell.Sync.SelectedNode = Row(project, App + "/Task Configuration/MainTask");
        Assert.Equal(["PLC_PRG", "Simulation"], Assert.IsType<TaskDetailRender>(shell.RenderedContent).Task.Pous);

        shell.Sync.SelectedNode = Row(project, "Device/EtherCAT_Master");
        Assert.Contains(Assert.IsType<DeviceCatalog>(shell.RenderedContent).Connectors, c => c.Interface == "Common.Ethercat");

        shell.Sync.SelectedNode = Row(project, "GlobalTextList");
        Assert.Equal("TEXT ID", Assert.IsType<MemberTable>(shell.RenderedContent).NameHeader);

        shell.Sync.SelectedNode = Row(project, "Project Settings");
        Assert.Equal("SETTING", Assert.IsType<MemberTable>(shell.RenderedContent).NameHeader);
    }
}
