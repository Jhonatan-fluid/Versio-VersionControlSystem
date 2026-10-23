using Versio.Core.Projects;
using Versio.Plc.Codesys.FileBased;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// A CODESYS File-Based Storage folder described as a manifest, from the committed fixture's
/// real file list, and drawn by the neutral tree builder exactly as the panel draws it.
/// </summary>
public sealed class CodesysFileBasedLayoutTests
{
    private const string Folder = "fixtures/projects/RefrigeratorControl_FBS/RefrigeratorControl.fbsproj";

    private const string Application =
        "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3";

    private const string FbAlarm = Application + "^/Library/Alarm/POUs/FB_Alarm.fb.st";

    /// <summary>Every file in the fixture, as the app lists it: relative, forward slashes.</summary>
    private static IReadOnlyList<string> FixtureFiles()
    {
        var root = Path.Combine(TestPaths.Project("RefrigeratorControl_FBS"), "RefrigeratorControl.fbsproj");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static ProjectManifest Describe() =>
        CodesysProjectFormat.Instance.DescribeSourceProject(Folder, FixtureFiles())!;

    private static ProjectObject Object(ProjectManifest manifest, string path) =>
        Assert.Single(manifest.Objects, o => o.Path == path);

    [Fact]
    public void The_project_is_named_after_its_folder_and_written_by_this_vendor()
    {
        var manifest = Describe();

        Assert.Equal("RefrigeratorControl", manifest.Project);
        Assert.Equal(Core.Plc.PlcVendor.Codesys, manifest.Vendor);
        Assert.Equal(manifest.Objects.Count, manifest.ObjectCount);
    }

    [Fact]
    public void Codesys_state_is_not_an_object()
    {
        // .auxiliaries and .FBS_Project_Related are committed in the fixture; .sidecars is on
        // disk but ignored. None of them, the folder metadata, the workspace file or the
        // preferences is in the IDE's tree, so none is in this one.
        var paths = Describe().Objects.Select(o => o.Path).ToList();

        Assert.Contains(FixtureFiles(), f => f.StartsWith(".auxiliaries/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Split('/').Any(s => s.StartsWith('.')));
        Assert.DoesNotContain(paths, p => p.EndsWith(".fbsws", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.StartsWith("Preferences/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Application + "^/PLC_PRG.prg.st", "PLC_PRG", "POU")]
    [InlineData(Application + "^/Signals.pou.xml.v3", "Signals", "POU")]
    [InlineData(FbAlarm, "FB_Alarm", "POU")]
    [InlineData(FbAlarm + "^/M_Reset.meth.st", "M_Reset", "METHOD")]
    [InlineData(FbAlarm + "^/P_Active.prop.st", "P_Active", "PROPERTY")]
    [InlineData(Application + "^/Library/Alarm/DUTs/E_AlarmState.enum", "E_AlarmState", "DUT")]
    [InlineData(Application + "^/Library/Alarm/DUTs/ST_AlarmSettings.struct", "ST_AlarmSettings", "DUT")]
    [InlineData(Application + "^/Glob_Var.gvl", "Glob_Var", "GVL")]
    [InlineData(Application + "^/PersistentVars.varpersistent.xml.v3", "PersistentVars", "GVL_PERSISTENT")]
    [InlineData(Application + "^/Libraries.json", "Libraries", "LIBRARY_MANAGER")]
    [InlineData(Application + "^/Task Configuration.taskconfig.xml.v3^/MainTask.task.xml.v3", "MainTask", "TASK")]
    [InlineData("Device.device.xml.v3", "Device", "DEVICE")]
    [InlineData("Device.device.xml.v3^/Plc Logic.plclogic.xml.v3", "Plc Logic", "PLC_LOGIC")]
    [InlineData("Device.device.xml.v3^/EtherCAT_Master.device.xml.v3^/BNI_XG3_508_0C5_R015.device.xml.v3^/__3cEmpty__3e 0.slotdevice.xml.v3", "<Empty> 0", "DEVICE_SLOT")]
    [InlineData("Project Settings.workspace.xml.v3", "Project Settings", "PROJECT_SETTINGS")]
    public void Each_object_is_named_and_typed_from_its_file(string path, string name, string type)
    {
        var obj = Object(Describe(), path);

        Assert.Equal(name, obj.Name);
        Assert.Equal(type, obj.Type);
    }

    [Theory]
    [InlineData(FbAlarm + "^/M_Reset.meth.st", FbAlarm)]
    [InlineData(FbAlarm + "^/Private/_M_Timers.meth.st", FbAlarm)]
    [InlineData(FbAlarm, Application)]
    [InlineData(Application, "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3")]
    [InlineData("Device.device.xml.v3^/Plc Logic.plclogic.xml.v3", "Device.device.xml.v3")]
    [InlineData("Device.device.xml.v3", "")]
    [InlineData("GlobalTextList.globaltextlist.xml.v3", "")]
    public void An_object_belongs_to_the_nearest_caret_folder_above_it(string path, string owner)
    {
        // Private/ is a folder the engineer made inside FB_Alarm, not an owner: the method in it
        // is still FB_Alarm's.
        Assert.Equal(owner, Object(Describe(), path).Parent);
    }

    [Fact]
    public void The_tree_reads_device_plc_logic_application_and_fb_alarm_keeps_its_private_folder()
    {
        // The whole tree, as the panel and the explorer ask for it; the default prunes what is
        // unchanged, which in a list of paths with no git status is everything.
        var tree = ProjectTreeBuilder.Build(Describe(), Folder, [], new ProjectTreeOptions { ChangedOnly = false });

        var devices = Assert.Single(tree.Children, n => n.Name == CodesysFileBasedLayout.DevicesView);
        var device = Assert.Single(devices.Children, n => n.Name == "Device");
        var plcLogic = Assert.Single(device.Children, n => n.Name == "Plc Logic");
        var application = Assert.Single(plcLogic.Children, n => n.Name == "Application");
        var library = Assert.Single(application.Children, n => n.Name == "Library");
        var fbAlarm = Assert.Single(Flatten(library), n => n.Name == "FB_Alarm");

        var @private = Assert.Single(fbAlarm.Children, n => n.Name == "Private");
        Assert.Equal(ProjectNodeKind.Folder, @private.Kind);
        Assert.Contains(@private.Children, n => n.Name == "_M_Timers");
        Assert.Contains(fbAlarm.Children, n => n.Name == "M_Reset");

        // The owner's own ^ directory is not a folder anybody made, and must not become a row.
        Assert.DoesNotContain(Flatten(tree), n => n.Name.EndsWith('^'));
    }

    /// <summary>
    /// The owner's library, <c>FsLibrary.fbslib</c>, as its file list stood on 2026-10-23: the
    /// library's folders, its manager and settings, and a device holding the test application,
    /// all at the folder's root.
    /// </summary>
    private static readonly string[] LibraryFiles =
    [
        "00_Template/.folder_meta.json",
        "00_Template/FB_Template.fb.st",
        "00_Template/FB_Template.fb.st^/Reset.meth.st",
        "01_Core/Alarm/FB_Alarm.fb.st",
        "CODESYS_Control_Win_V3_x64.device.xml.v3",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/Libraries.json",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/Main.prg.st",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/PersistentVars.varpersistent.xml.v3",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/Symbols.symbolconfig.xml.v3",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/Task Configuration.taskconfig.xml.v3",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/TestAlarm.prg.st",
        "CODESYS_Control_Win_V3_x64.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/TestLogger.prg.st",
        "Libraries.json",
        "Project Settings.workspace.xml.v3",
        "ProjectInfo.json",
        "Workspace.fbsws",
    ];

    [Fact]
    public void A_library_is_drawn_as_codesys_draws_it_the_pou_pool_apart_from_the_device_tree()
    {
        // Before 2026-10-23 the root read 00_Template, 01_Core and a POUs folder holding the
        // device: the neutral builder took the root-level files for the pool and the foldered
        // objects for the device tree, which for a File-Based Storage library is backwards.
        const string library = "FsLibrary.fbslib";
        var manifest = CodesysFileBasedLayout.Describe(library, LibraryFiles);

        var tree = ProjectTreeBuilder.Build(manifest, library, [], new ProjectTreeOptions { ChangedOnly = false });

        Assert.Equal(
            [CodesysFileBasedLayout.DevicesView, ProjectTreeBuilder.PoolGroupName],
            tree.Children.Select(n => n.Name));

        var pous = tree.Children[1];
        Assert.Equal(
            ["00_Template", "01_Core", "Libraries", "Project Settings", "ProjectInfo"],
            pous.Children.Select(n => n.Name));

        var devices = tree.Children[0];
        var device = Assert.Single(devices.Children);
        Assert.Equal("CODESYS_Control_Win_V3_x64", device.Name);
        var application = Assert.Single(Assert.Single(device.Children, n => n.Name == "Plc Logic").Children);
        Assert.Equal("Application", application.Name);
        Assert.Equal(
            ["Libraries", "Main", "PersistentVars", "Symbols", "Task Configuration", "TestAlarm", "TestLogger"],
            application.Children.Select(n => n.Name));
    }

    [Fact]
    public void A_kind_nobody_has_seen_is_still_an_object_typed_by_its_own_word()
    {
        var manifest = CodesysFileBasedLayout.Describe("x.fbsproj", ["Recipe.recipemanager.xml.v3", "Notes.txt"]);

        Assert.Equal("RECIPEMANAGER", Object(manifest, "Recipe.recipemanager.xml.v3").Type);
        Assert.Equal("TXT", Object(manifest, "Notes.txt").Type);
    }

    [Fact]
    public void A_child_whose_owner_file_is_missing_stays_where_its_path_puts_it()
    {
        var manifest = CodesysFileBasedLayout.Describe("x.fbsproj", ["Gone.fb.st^/M.meth.st"]);

        Assert.Equal("", Object(manifest, "Gone.fb.st^/M.meth.st").Parent);
    }

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node) =>
        node.Children.SelectMany(c => Flatten(c).Prepend(c));
}
