using Versio.Core.Plc;
using Versio.Core.Projects;

namespace Versio.Plc.Codesys.FileBased;

/// <summary>
/// A CODESYS File-Based Storage folder, described as the manifest the project tree draws.
/// </summary>
/// <remarks>
/// <para>
/// <b>The folder already is the project.</b> CODESYS writes one or more files per object into
/// a tree that mirrors the IDE's, so there is nothing to export: the tree is read straight off
/// the file list. Every rule here was read from the committed fixture
/// <c>fixtures/projects/RefrigeratorControl_FBS/</c>, CODESYS 3.5.22.40 with FBS 1.0.0.0. See
/// <c>docs/fbs/PLAN.md</c> §2.
/// </para>
/// <para>
/// <b>Kind from the compound extension.</b> <c>PLC_PRG.prg.st</c> is a program,
/// <c>FB_Alarm.fb.st</c> a function block, <c>Signals.pou.xml.v3</c> a graphical POU,
/// <c>Plc Logic.plclogic.xml.v3</c> the PLC logic node. A kind this table does not know is
/// still an object, typed by its own word: dropping a file a reviewer would look for is the
/// failure this tree exists to prevent.
/// </para>
/// <para>
/// <b>Owners from the <c>^</c> folders.</b> An object's children sit in a folder named after
/// the owner's file plus <c>^</c>. A user folder inside an owner (<c>FB_Alarm.fb.st^/Private/</c>)
/// is an ordinary directory, so the owner is the nearest <c>^</c> folder above the file, not
/// necessarily its own directory. <see cref="ProjectTreeBuilder"/> already draws a member's
/// folders inside its owner and drops the owner's own directory, the same as it does for a
/// schema-9 mirror, so no neutral code learns what <c>^</c> means.
/// </para>
/// <para>
/// <b>Identity is the path.</b> The real object GUID is in each file's header, but reading it
/// would be IO, and the format is handed a list of paths and nothing else. The tree only needs
/// identity to stay stable from one refresh to the next, and a path does.
/// </para>
/// </remarks>
public static class CodesysFileBasedLayout
{
    /// <summary>The folder extensions CODESYS writes for a project and for a library project.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".fbsproj", ".fbslib"];

    private const string OwnerSuffix = "^";

    /// <summary>
    /// Kinds as CODESYS spells them in a file name, to the type words the rest of the CODESYS
    /// pack and the IEC table already name. Everything here was seen in the fixture except the
    /// rows marked otherwise, which come from the 0.9 whitepaper and are unconfirmed.
    /// </summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prg"] = "POU",
        ["fb"] = "POU",
        ["pou"] = "POU",
        ["meth"] = "METHOD",
        ["prop"] = "PROPERTY",
        ["gvl"] = "GVL",
        ["varpersistent"] = "GVL_PERSISTENT",
        ["enum"] = "DUT",
        ["struct"] = "DUT",
        ["device"] = "DEVICE",
        ["slotdevice"] = "DEVICE_SLOT",
        ["plclogic"] = "PLC_LOGIC",
        ["application"] = "APPLICATION",
        ["taskconfig"] = "TASK_CONFIGURATION",
        ["task"] = "TASK",
        ["visual"] = "VISUALISATION",
        ["visualmanager"] = "VISUALISATION_MANAGER",
        ["targetvisualization"] = "TARGET_VISUALISATION",
        ["globaltextlist"] = "GLOBAL_TEXTLIST",
        ["workspace"] = "PROJECT_SETTINGS",

        // Whitepaper only: not in the fixture.
        ["action"] = "ACTION",
        ["transition"] = "TRANSITION",
        ["itf"] = "INTERFACE",
        ["union"] = "DUT",
        ["alias"] = "DUT",
    };

    /// <summary>The library manager's fixed file name. JSON, one per manager.</summary>
    private const string LibraryManagerFile = "Libraries.json";

    /// <summary>CODESYS's name for the pane that holds the device tree.</summary>
    public const string DevicesView = "Devices";

    /// <summary>The compound extension of a device's own file, which heads the Devices pane.</summary>
    private const string DeviceFile = ".device" + CodesysV3File.Extension;

    public static ProjectManifest Describe(string projectPath, IReadOnlyList<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(relativeFiles);

        var files = relativeFiles
            .Select(f => f.Replace('\\', '/'))
            .Where(IsObjectFile)
            .ToList();
        var present = files.ToHashSet(StringComparer.Ordinal);

        var objects = files
            .Select(path => new ProjectObject
            {
                Guid = path,
                Name = CodesysV3File.ObjectName(path),
                Type = TypeOf(path),
                Path = path,
                Parent = OwnerOf(path, present) ?? string.Empty,
                View = ViewOf(path),
            })
            .ToList();

        return new ProjectManifest
        {
            Vendor = PlcVendor.Codesys,
            Project = CodesysV3File.ObjectName(projectPath.TrimEnd('/', '\\')),
            ObjectCount = objects.Count,
            Objects = objects,
        };
    }

    /// <summary>
    /// Whether a file is one of the project's objects rather than CODESYS's own state.
    /// </summary>
    /// <remarks>
    /// Anything under a dot-folder (<c>.auxiliaries</c>, <c>.sidecars</c>,
    /// <c>.FBS_Project_Related</c>) or named with a leading dot (<c>.folder_meta.json</c>) is
    /// hidden in the IDE, and the whitepaper says so of the prefix. The workspace file and
    /// <c>Preferences/</c> are compiler and editor settings, not objects in the IDE's tree.
    /// </remarks>
    private static bool IsObjectFile(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s.StartsWith('.')))
        {
            return false;
        }

        if (segments.Length == 1 && segments[0].EndsWith(".fbsws", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(segments.Length > 1 && segments[0].Equals("Preferences", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The IDE pane a file's object is shown in: Devices for a device at the folder's root and
    /// everything under its <c>^</c> folder, POUs for the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CODESYS draws a project in two panes, and the folder root mixes them.</b> A library
    /// (<c>FsLibrary.fbslib</c>, 2026-10-23) keeps <c>CODESYS_Control_Win_V3_x64.device.xml.v3</c>
    /// — its test application, with <c>Main</c>, <c>TestAlarm</c> and the tasks — at the root
    /// beside the library's own folders, its manager <c>Libraries.json</c>, the project
    /// settings and <c>ProjectInfo.json</c>. Left to the neutral builder, the root-level files
    /// were filed as the pool and the device went under POUs with them, while the folders that
    /// <i>are</i> the pool sat beside it ungrouped. The owner asked for CODESYS's own two views.
    /// </para>
    /// <para>
    /// <b>Only a device heads the Devices pane.</b> The IDE has nothing else at the top of it:
    /// Plc Logic, the application and its objects all hang off a device. Every other root
    /// object — folders of POUs, the project-level library manager, the project settings,
    /// project information, a global text list — is in the POU pool, which CODESYS's own tree
    /// shows apart from the device on the same library. The first segment decides, so a file
    /// deep in a device's <c>^</c> folder is in Devices without walking its owners.
    /// </para>
    /// </remarks>
    private static string ViewOf(string path)
    {
        var top = path.Split('/', 2)[0];
        var device = top.EndsWith(OwnerSuffix, StringComparison.Ordinal) ? top[..^OwnerSuffix.Length] : top;
        return device.EndsWith(DeviceFile, StringComparison.OrdinalIgnoreCase)
            ? DevicesView
            : ProjectTreeBuilder.PoolGroupName;
    }

    /// <summary>The type word for a file, from the kind between its name and its storage suffix.</summary>
    private static string TypeOf(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        if (file.Equals(LibraryManagerFile, StringComparison.OrdinalIgnoreCase))
        {
            return "LIBRARY_MANAGER";
        }

        var stem = file.EndsWith(CodesysV3File.Extension, StringComparison.OrdinalIgnoreCase)
            ? file[..^CodesysV3File.Extension.Length]
            : file.EndsWith(".st", StringComparison.OrdinalIgnoreCase) ? file[..^".st".Length] : file;
        var dot = stem.LastIndexOf('.');
        var kind = dot < 0 ? string.Empty : stem[(dot + 1)..];

        return Kinds.TryGetValue(kind, out var type)
            ? type
            : kind.Length > 0 ? kind.ToUpperInvariant() : "FILE";
    }

    /// <summary>
    /// The owner's path: the nearest folder above the file whose name ends in <c>^</c>, with the
    /// <c>^</c> taken off. Null when there is none, or when no such file is in the list, in which
    /// case the object stays where its path puts it rather than hanging off nothing.
    /// </summary>
    private static string? OwnerOf(string path, HashSet<string> present)
    {
        var segments = path.Split('/');
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            if (segments[i].EndsWith(OwnerSuffix, StringComparison.Ordinal))
            {
                var owner = string.Join('/', segments[..i].Append(segments[i][..^OwnerSuffix.Length]));
                return present.Contains(owner) ? owner : null;
            }
        }

        return null;
    }
}
