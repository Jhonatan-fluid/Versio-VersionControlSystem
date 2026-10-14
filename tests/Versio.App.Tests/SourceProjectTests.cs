using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A source project in the running shell: a CODESYS File-Based Storage folder, shown as a
/// project with its objects, and never bound, exported or gated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the "never" half is tested this hard.</b> The shortcut this replaced was registering
/// the folder as an ordinary project with no runner behind it. Today that project is
/// auto-adopted, fails its export, and the failed lock blocks every commit in the repository.
/// So these open a real repository, look for a binding that must not exist, and commit through
/// the shell with a file inside the folder changed. See <c>docs/fbs/PLAN.md</c> §1.
/// </para>
/// <para>
/// The folder is small and hand-made in the fixture's real layout: three objects, one of them
/// nested in a <c>^</c> folder, plus CODESYS state that must stay out of the tree.
/// </para>
/// </remarks>
public sealed class SourceProjectTests
{
    private const string Folder = "plc/Machine.fbsproj";
    private const string Program = Folder + "/Device.device.xml.v3^/PLC_PRG.prg.st";

    private static void GivenACommittedSourceProject(TempRepo repo)
    {
        void Write(string relative, string text)
        {
            var path = Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Write(Folder + "/Device.device.xml.v3", "__V3_META__\n{}\n__V3_CONTENT__\n<Single />\n");
        // Written as File-Based Storage 1.0 writes it: a header naming the object, then the
        // declaration and body with no divider, then the closing keyword.
        Write(Program, "(* METADATA\n{\n\t\"v3Meta\": {}\n}\n*)\nPROGRAM PLC_PRG\nVAR\n\tx : BOOL;\nEND_VAR\nx := TRUE;\nEND_PROGRAM\n");
        Write(Folder + "/Device.device.xml.v3^/Glob_Var.gvl", "(* METADATA\n{}\n*)\nVAR_GLOBAL\n\ty : INT;\nEND_VAR\n");
        Write(Folder + "/.auxiliaries/cache", "not an object");
        Write(Folder + "/Workspace.fbsws", "{}");

        repo.Git("add -A");
        repo.Git("commit -q -m fbs");
    }

    private static string LastCommitFiles(TempRepo repo)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "show --name-only --pretty=format:%s HEAD",
            WorkingDirectory = repo.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });

        var output = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return output;
    }

    private static async Task OpenAndSettleAsync(TempRepo repo, ShellViewModel shell)
    {
        await shell.OpenAsync(repo.Path);

        // Opening may write repository files of its own (ignore rules); committing them is what
        // leaves a tree with genuinely nothing changed.
        repo.Git("add -A");
        repo.Git("commit -q --allow-empty -m settled");
        await shell.RefreshAsync();
    }

    [Fact]
    public async Task Opening_writes_the_machine_local_ignore_rules_a_source_project_never_got()
    {
        // A source project never goes through adoption, so nothing wrote its vendor's
        // scratch rules: on a real library every IDE open dressed the project in "modified"
        // cache rows, and the per-user sidecar put a username and machine name into history
        // by its filename alone (reported 2026-10-14). The top-up pass writes them now —
        // and it must run even here, where the repository has no bindings at all.
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();

        await OpenAndSettleAsync(repo, shell);

        var rules = File.ReadAllText(Path.Combine(repo.Path, ".gitignore"));
        Assert.Contains(Folder + "/.sidecars/", rules, StringComparison.Ordinal);
        Assert.Contains(Folder + "/.auxiliaries/", rules, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_folder_is_a_row_read_from_its_files_and_no_binding_is_written()
    {
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();

        await OpenAndSettleAsync(repo, shell);

        var row = Assert.Single(shell.Sync.Projects);
        Assert.Equal(Folder, row.Path);
        Assert.True(row.ReadFromFiles);
        Assert.Null(row.BindingId);
        Assert.Equal(SyncViewModel.SourceProjectState, row.State);
        Assert.Equal("1 project, shown from its own files.", shell.Sync.Summary);

        var config = Path.Combine(repo.Path, ".versio", "repo.json");
        Assert.True(!File.Exists(config) || !File.ReadAllText(config).Contains("fbsproj", StringComparison.OrdinalIgnoreCase),
            "a source project must never be written as a binding");
    }

    [Fact]
    public async Task Unchanged_it_is_listed_in_the_quiet_note_and_the_panel_does_not_say_nothing_is_tracked()
    {
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();

        await OpenAndSettleAsync(repo, shell);

        Assert.Empty(shell.Sync.Tree);
        var note = Assert.Single(shell.Sync.InSyncProjects);
        Assert.Equal("Machine.fbsproj", note.Name);
        Assert.Equal(SyncViewModel.SourceProjectState, note.State);
        Assert.NotEqual("No PLC projects tracked in this repository.", shell.Sync.TreeSummary);
    }

    [Fact]
    public async Task A_changed_file_shows_the_project_with_its_objects_and_commits_without_a_gate()
    {
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();
        await OpenAndSettleAsync(repo, shell);

        File.AppendAllText(Path.Combine(repo.Path, Program.Replace('/', Path.DirectorySeparatorChar)), "// edited\n");
        await shell.RefreshAsync();

        var project = Assert.Single(shell.Sync.Tree, r => r.Node.Kind == ProjectNodeKind.Project);
        Assert.Equal(Folder, project.Node.Path);
        var device = Assert.Single(project.Node.Children, n => n.Name == "Device");
        var program = Assert.Single(device.Children, n => n.Name == "PLC_PRG");
        Assert.Equal(ChangeKind.Modified, program.Status);
        Assert.Contains(device.Children, n => n.Name == "Glob_Var");
        Assert.DoesNotContain(project.Node.Children, n => n.Name.StartsWith('.') || n.Name.Contains("Workspace", StringComparison.Ordinal));

        // The commit an ordinary project with a failed export would be refused.
        shell.CommitMessage = "edit inside a source project";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(() => shell.Pending.Count == 0, "the commit landed");
        Assert.Contains("PLC_PRG.prg.st", LastCommitFiles(repo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_the_project_shows_its_own_card_not_a_binary_projects()
    {
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();
        await OpenAndSettleAsync(repo, shell);

        File.AppendAllText(Path.Combine(repo.Path, Program.Replace('/', Path.DirectorySeparatorChar)), "// edited\n");
        await shell.RefreshAsync();

        shell.Sync.SelectedNode = Assert.Single(shell.Sync.Tree, r => r.Node.Kind == ProjectNodeKind.Project);

        var card = Assert.IsType<ProjectSummaryRender>(shell.RenderedContent);
        Assert.Contains(card.Facts, f => f.Label == "Vendor" && f.Value == "CODESYS 3.5");
        Assert.Contains(card.Facts, f => f.Label == "Stored as");
        Assert.DoesNotContain(card.Facts, f => f.Label is "Mirror" or "Toolchain" or "Coverage");
        Assert.Contains(card.Facts, f => f.Label == "Objects" && f.Value.Contains("POU", StringComparison.Ordinal));
        Assert.DoesNotContain("binary", shell.SelectedDiff, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_program_reads_as_tia_blocks_do_its_variables_as_a_table_over_its_code()
    {
        // The owner, 2026-10-10: render CODESYS the way a TIA block renders, variables as a table
        // on top and the logic under it.
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();
        await OpenAndSettleAsync(repo, shell);

        var file = Path.Combine(repo.Path, Program.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(file, File.ReadAllText(file).Replace("x := TRUE;", "x := FALSE;", StringComparison.Ordinal));
        await shell.RefreshAsync();

        var project = Assert.Single(shell.Sync.Tree, r => r.Node.Kind == ProjectNodeKind.Project);
        var device = Assert.Single(project.Children, r => r.Node.Name == "Device");
        shell.Sync.SelectedNode = Assert.Single(device.Children, r => r.Node.Name == "PLC_PRG");

        var shown = Assert.IsType<InterfaceRender>(shell.RenderedContent);
        Assert.Equal("PLC_PRG", shown.Table.Name);
        Assert.Equal(["Static", "x"], shown.Table.Members.Select(m => m.Name));

        // Only the code changed: the code under the table is marked, and the table, with no
        // member changed, carries no note (the owner, 2026-10-11: "no need for that note at all").
        await Eventually.Until(
            () => shell.RenderedContent is InterfaceRender { Source: { } s } && s.Rows.OfType<Versio.Core.Iec.SourceRemovedLine>().Any(),
            "the code under the table is marked");
        Assert.False(((InterfaceRender)shell.RenderedContent!).Table.HasChangeSummary);

        // The code sits under the table, without the declaration repeated above it.
        Assert.Contains(shown.Source!.Rows.OfType<Versio.Core.Iec.SourceCodeLine>(), l => l.Text == "x := FALSE;");
        Assert.DoesNotContain(shown.Source.Rows.OfType<Versio.Core.Iec.SourceCodeLine>(), l => l.Text.StartsWith("VAR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_global_list_is_a_table_with_no_access_column()
    {
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();
        await OpenAndSettleAsync(repo, shell);

        File.AppendAllText(Path.Combine(repo.Path, (Folder + "/Device.device.xml.v3^/Glob_Var.gvl").Replace('/', Path.DirectorySeparatorChar)), "\n");
        await shell.RefreshAsync();

        var project = Assert.Single(shell.Sync.Tree, r => r.Node.Kind == ProjectNodeKind.Project);
        var device = Assert.Single(project.Children, r => r.Node.Name == "Device");
        shell.Sync.SelectedNode = Assert.Single(device.Children, r => r.Node.Name == "Glob_Var");

        var table = Assert.IsType<MemberTable>(shell.RenderedContent);
        Assert.True(table.IsVariableList);
        Assert.False(table.HasExternalAccess);
        Assert.Equal("y", Assert.Single(table.Members).Name);
    }

    [Fact]
    public async Task The_explorer_reads_the_same_objects()
    {
        using var repo = new TempRepo(identity: true);
        GivenACommittedSourceProject(repo);
        using var shell = TestShell.Build();
        await OpenAndSettleAsync(repo, shell);

        var project = Assert.Single(shell.Sync.ReadProjectsForExplorer());

        Assert.Equal(Folder, project.ProjectPath);
        Assert.Equal(Folder, project.ExportPath);
        Assert.Equal(
            ["Device", "Glob_Var", "PLC_PRG"],
            project.Manifest!.Objects.Select(o => o.Name).Order(StringComparer.Ordinal));
    }
}
