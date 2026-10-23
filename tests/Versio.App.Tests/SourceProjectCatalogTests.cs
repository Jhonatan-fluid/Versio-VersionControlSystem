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
    // Under the pane CODESYS shows it in: the device tree is Devices, the rest is POUs.
    private const string App = "Devices/Device/Plc Logic/Application";

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

        shell.Sync.SelectedNode = Row(project, "Devices/Device/EtherCAT_Master");
        Assert.Contains(Assert.IsType<DeviceCatalog>(shell.RenderedContent).Connectors, c => c.Interface == "Common.Ethercat");

        shell.Sync.SelectedNode = Row(project, "POUs/GlobalTextList");
        Assert.Equal("TEXT ID", Assert.IsType<MemberTable>(shell.RenderedContent).NameHeader);

        shell.Sync.SelectedNode = Row(project, "POUs/Project Settings");
        Assert.Equal("SETTING", Assert.IsType<MemberTable>(shell.RenderedContent).NameHeader);
    }

    [Fact]
    public async Task EmptyObjectPaneFlowTests_an_object_with_nothing_in_its_own_file_says_so()
    {
        // Plc Logic is one empty archive element. It drew as that line of XML until 2026-10-23;
        // now the note, the same wording the web viewer draws. Its children are under it, so
        // the note says where to look.
        using var repo = new TempRepo(identity: true);
        using var shell = await OpenWithOneLibraryChangedAsync(repo);
        var project = Assert.Single(shell.Sync.Tree, r => r.Node.Kind == ProjectNodeKind.Project).Children;

        shell.Sync.SelectedNode = Row(project, "Devices/Device/Plc Logic");

        var note = Assert.IsType<MarkdownRender>(shell.RenderedContent);
        var paragraph = Assert.IsType<Versio.Core.Text.MdParagraph>(Assert.Single(note.Blocks));
        Assert.Equal(Versio.Core.Projects.EmptyObjectNote.For(hasChildren: true), string.Concat(paragraph.Runs.Select(r => r.Text)));

        // A container that does hold settings keeps its file.
        shell.Sync.SelectedNode = Row(project, App);
        Assert.IsNotType<MarkdownRender>(shell.RenderedContent);
    }
}
