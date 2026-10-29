using Versio.Core.Plc;
using Versio.Core.Projects;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// TIA Portal's project-file conventions.
/// </summary>
/// <remarks>
/// The second concrete <see cref="IPlcProjectFormat"/>. Everything here is this vendor's:
/// the extensions, the directory payload, the object-type names, and the planner. Sync
/// never reads this type.
/// </remarks>
public sealed class TiaPortalProjectFormat : IPlcProjectFormat
{
    public static readonly TiaPortalProjectFormat Instance = new();

    public PlcVendor Vendor => PlcVendor.TiaPortal;

    public string DisplayName => "TIA Portal";

    public string LogCategory => TiaPortalLog.Category;

    /// <inheritdoc />
    /// <remarks>
    /// The extension is the release: <c>.ap21</c> opens only in V21, and an older Portal
    /// refuses it outright while a newer one offers an irreversible upgrade. So the pill
    /// says the number rather than only the vendor. Anything that does not read as
    /// <c>.apNN</c> / <c>.alNN</c> falls back to the plain name rather than guessing.
    /// </remarks>
    public string ProjectLabelFor(string projectPath) =>
        ReleaseOf(projectPath) is { } release ? $"{DisplayName} {release}" : DisplayName;

    /// <inheritdoc />
    /// <remarks>
    /// The release in the extension, and the probe's profile only for a path that carries
    /// none — which this format does not claim, so in practice the binding records the
    /// project's own release and never the newest Portal the machine happens to have.
    /// </remarks>
    public string? ProfileFor(string projectPath, string? toolchainProfile) =>
        ReleaseOf(projectPath) ?? toolchainProfile;

    /// <summary>
    /// The Portal release a project file opens in, from its extension: <c>.ap20</c> is
    /// <c>V20</c>. Null when the extension does not carry a number.
    /// </summary>
    /// <remarks>
    /// This is the one fact that decides which installed Portal may open the project, and it
    /// is on the file, not in a setting. Until 2026-10-29 the runner opened every project with
    /// whichever Portal the toolchain had picked — the newest it could see — and a V20 project
    /// on a machine with V20 and V21 installed was handed to V21, which refused it as a
    /// compatibility error (an upgrade is the only way V21 opens it, and Versio never
    /// upgrades). The runner asks this first now.
    /// </remarks>
    public static string? ReleaseOf(string projectPath)
    {
        var ext = System.IO.Path.GetExtension(projectPath);
        return ext.Length == 5
               && (ext.StartsWith(".ap", StringComparison.OrdinalIgnoreCase)
                   || ext.StartsWith(".al", StringComparison.OrdinalIgnoreCase))
               && char.IsAsciiDigit(ext[3]) && char.IsAsciiDigit(ext[4])
            ? $"V{ext[3..]}"
            : null;
    }

    /// <remarks>
    /// Measured 2026-08-19: a warm probe of CleaningStation is 42 s, a warm export 37 s.
    /// The host still uses a twenty-minute timeout because a 488 MB project and a first
    /// AllowList dialog (8 min 53 s unanswered) are not the first vendor's 20-second session.
    /// </remarks>
    public PlcCapabilities Capabilities => new()
    {
        TextualExport = true,
        TextualImport = true,
        ObjectCreation = true,
        Headless = true,
        // Exclusive Projects.Open, and the payload is a directory (~500 MB on the
        // gate fixture). A copy-aside export is the first vendor's sidecar trick;
        // it is not valid here. Occupancy waits both directions instead.
        ExportHeldViaCopy = false,
        TypicalStartupCost = TimeSpan.FromSeconds(45),
    };

    /// <remarks>
    /// <c>.ap17</c> through <c>.ap21</c> are working projects. <c>.alXX</c> archives are
    /// seen and refused rather than imported — same shape as a <c>.library</c> on the
    /// first vendor: detection without a drive path is the useful answer.
    /// </remarks>
    public IReadOnlyList<string> ProjectExtensions =>
        [".ap17", ".ap18", ".ap19", ".ap20", ".ap21", ".al17", ".al18", ".al19", ".al20", ".al21"];

    public bool IsTemplate(string path)
    {
        // Archives (.alXX) are this vendor's packed project. Adopting one would bind
        // Versio to a file TIA will not open as a working tree. Treated as a template
        // so the scanner sees them and the adoption path refuses, the same bargain
        // *_template.project makes on the first vendor.
        var ext = System.IO.Path.GetExtension(path);
        return ext.StartsWith(".al", StringComparison.OrdinalIgnoreCase);
    }

    public string ConventionalExportPath(string projectPath)
    {
        var normalised = projectPath.Replace('\\', '/');
        var ext = System.IO.Path.GetExtension(normalised);
        return string.IsNullOrEmpty(ext)
            ? normalised + ".export"
            : normalised[..^ext.Length];
    }

    public string? ConventionalTemplatePath(string projectPath) => null;

    /// <inheritdoc />
    /// <remarks>
    /// The <c>.apXX</c> file is a 150 KB pointer. The program lives in the directory
    /// beside it, and TIA cannot open a copy of the pointer alone. Hashing, backing up
    /// or exporting that file would be the first vendor's copy pipeline applied to the
    /// wrong thing.
    /// </remarks>
    public string PayloadPathFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return projectPath;
        }

        var dir = System.IO.Path.GetDirectoryName(projectPath);
        return string.IsNullOrEmpty(dir) ? projectPath : dir;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Logs/</c> and <c>tmp/</c> churn while the program does not. <c>Vci/</c> is
    /// Siemens's own git exporter sitting inside the tree — hashing it would make
    /// somebody else's workspace look like a program change. <c>System/</c> is the
    /// program — skipping it would make a 150 KB pointer the whole hash of a
    /// half-gigabyte project. The occupancy sidecar <c>.info</c> and
    /// <c>System/~PEData.*</c> appear while Openness holds the project and vanish on
    /// close; hashing them would mark every in-flight export as a program change.
    /// </remarks>
    public bool CountsTowardProjectHash(string relativePath)
    {
        var normalised = relativePath.Replace('\\', '/');
        var parts = normalised.Split('/');
        var first = parts[0];
        var file = parts[^1];
        if (string.Equals(first, "Logs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "tmp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "Vci", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (file.StartsWith('~'))
        {
            return false;
        }

        return parts.Length != 1
               || !file.EndsWith(".info", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Beside the <c>.apXX</c>, TIA writes <c>&lt;stem&gt;.info</c> while the project is
    /// held and removes it on close. Measured 2026-08-19 on CleaningStation under an
    /// Openness <c>Projects.Open</c>: the sidecar appeared 16 s after open, vanished
    /// when the host exited. <c>System/~PEData.1</c> appears too; that is a transient
    /// inside the program folder, not the marker the occupancy probe watches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is not always removed.</b> Seen 2026-10-28 on a V20 project in the Portal
    /// window (FINDINGS, same date): a <c>.info</c> written two days earlier was neither
    /// rewritten when the project was opened again nor deleted when it was closed, while
    /// <c>System/~PEData.3</c> did go on close. And seen again 2026-10-29 on a project that
    /// had never been opened in a window at all: the session that created it wrote the
    /// marker, and a folder copy carried it into the repository. So a leftover reads Held
    /// for as long as it sits there, with no pid for the stale policy to probe. That is why
    /// the body is parsed (<see cref="ParseOccupancy"/>) and why the refusal names the file.
    /// </para>
    /// <para>
    /// It is also why <see cref="HolderProcessNames"/> names the Portal process: a leftover
    /// that names this machine, with no Portal running here, is a leftover and nothing else,
    /// and the probe says so without a person deleting anything. While a Portal <i>is</i>
    /// running the marker is still believed — it may be that window's — and the refusal
    /// still names the file.
    /// </para>
    /// </remarks>
    public string? OccupancyMarkerFor(string projectPath) =>
        string.IsNullOrEmpty(projectPath)
            ? null
            : System.IO.Path.ChangeExtension(projectPath, ".info");

    /// <summary>The one process that can hold a project on this machine.</summary>
    /// <remarks>
    /// <c>Siemens.Automation.Portal</c> is the Portal window and the headless Portal alike:
    /// Openness activates it as a COM server, and the host identifies its own instance by
    /// watching that name appear (<c>HostApp.CreatePortal</c>), the runner reaps it by that
    /// name, and the 2026-10-28 finding counted two of them by that name. Measured three
    /// ways, which is what a name on this list has to be: a wrong one reads "not running" for
    /// a Portal that is, and that is two writers on one project.
    /// </remarks>
    public IReadOnlyList<string> HolderProcessNames => ["Siemens.Automation.Portal"];

    public IReadOnlyList<string> TransientPatternsFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return [];
        }

        var normalised = projectPath.Replace('\\', '/');
        var dir = PayloadPathFor(normalised).Replace('\\', '/').TrimEnd('/');
        var stem = System.IO.Path.GetFileNameWithoutExtension(normalised);
        return
        [
            dir + "/Logs/",
            dir + "/tmp/",
            dir + "/Vci/",
            dir + "/" + stem + ".info",
            dir + "/System/~*",
        ];
    }

    /// <summary>
    /// The folders of Portal's own program store, scoped to this project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listed from what the gate fixture actually commits (<c>git ls-files</c> over
    /// CleaningStation, 2026-09-14): <c>System/</c> is <c>PEData.plf</c> and
    /// <c>PEData.idx</c>, the program itself at ~445 MB; <c>IM/</c> is a search index
    /// (<c>.cfs</c>, <c>.tis</c>, <c>.fdt</c> …); <c>XRef/</c> is a <c>.db</c>;
    /// <c>AdditionalFiles/</c> and <c>UserFiles/</c> hold archives, <c>.rtid</c> files and
    /// Portal-written support files. Nothing in any of them is something a person line-merges.
    /// </para>
    /// <para>
    /// <b>Named folders, not the whole directory.</b> The mirror lives inside the project
    /// folder — <c>plc/Station/Station/</c> beside <c>plc/Station/System/</c> — so a
    /// <c>plc/Station/**</c> rule would make the reviewable text binary, which is the one thing
    /// the mirror exists to prevent; and for a project at the repository root that rule is
    /// <c>**</c>. <c>ProjectInfo.txt</c> stays text by being left out. <c>Logs/</c>,
    /// <c>tmp/</c> and <c>Vci/</c> are ignored rather than stored (see
    /// <see cref="TransientPatternsFor"/>). <c>src/</c> is among the measured top-level names
    /// but carries nothing in the fixture, so its contents were never seen and it is not
    /// claimed. A folder a later Portal version adds is not covered here; the commit's
    /// large-file check still asks before a big file lands outside LFS.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> BinaryPayloadPatternsFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return [];
        }

        // The pointer's own folder rather than PayloadPathFor: at the repository root that
        // answers with the pointer file itself, and "Station.ap21/System/**" names nothing.
        // GetDirectoryName hands back backslashes on Windows; git patterns never contain one.
        var dir = (System.IO.Path.GetDirectoryName(projectPath.Replace('\\', '/')) ?? string.Empty)
            .Replace('\\', '/')
            .TrimEnd('/');
        var prefix = dir.Length == 0 ? string.Empty : dir + "/";

        return [.. ProgramStoreFolders.Select(folder => prefix + folder + "/**")];
    }

    /// <summary>Portal's binary store, top-level. See <see cref="BinaryPayloadPatternsFor"/>.</summary>
    private static readonly string[] ProgramStoreFolders =
        ["System", "IM", "XRef", "AdditionalFiles", "UserFiles"];

    /// <remarks>
    /// Existence is the signal. The <c>.info</c> body was not parsed: a crashed session
    /// leaving the file behind is still Held, because guessing Free from an unknown
    /// format is how two writers would share a project. Stale recovery needs a pid,
    /// which this marker has not been shown to carry.
    /// </remarks>
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The marker is a small XML document: <c>&lt;InfoFile&gt;</c> holding <c>User</c>,
    /// <c>Computer</c> and <c>Time</c> (ISO 8601, UTC), and no pid. Read from a real one on
    /// 2026-10-28. Until then the body was not parsed, on the reasoning that stale recovery
    /// needs a pid and this file carries none. True, and beside the point: the user, the
    /// machine and above all the time are what let a person tell a live hold from a
    /// leftover — and a leftover is what this vendor produced that day (see
    /// <see cref="OccupancyMarkerFor"/>). A refusal that says "another session" when the
    /// marker says "you, two days ago" is withholding the one fact that would resolve it.
    /// </para>
    /// <para>
    /// Anything that does not parse is Held with no fields, never Free: an unreadable
    /// marker is still a marker, and guessing free is how two writers meet on one project.
    /// </para>
    /// </remarks>
    public ProjectOccupancy ParseOccupancy(string markerContents)
    {
        System.Xml.Linq.XElement? root;
        try
        {
            // Through SafeXml like every other document this vendor reads: no DTD, capped
            // size and depth. A marker is a file the IDE writes, not one Versio trusts.
            root = Versio.Core.Text.SafeXml.Parse((markerContents ?? string.Empty).TrimStart('\uFEFF')).Root;
        }
        catch (System.Xml.XmlException)
        {
            return new() { Verdict = OccupancyVerdict.Held };
        }

        if (root is null)
        {
            return new() { Verdict = OccupancyVerdict.Held };
        }

        var since = DateTimeOffset.TryParse(
            Field(root, "Time"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var time)
            ? time
            : (DateTimeOffset?)null;

        return new ProjectOccupancy
        {
            Verdict = OccupancyVerdict.Held,
            User = Field(root, "User"),
            Machine = Field(root, "Computer"),
            Since = since,
        };

        static string Field(System.Xml.Linq.XElement root, string name) =>
            root.Element(name)?.Value.Trim() ?? string.Empty;
    }

    public bool NeedsTextToPlan(string relativePath) =>
        TiaPortalImportPlanner.NeedsText(relativePath);

    public ImportPlan PlanImport(
        ProjectManifest? manifest,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string> departed) =>
        TiaPortalImportPlanner.Plan(manifest, mirror, departed);

    public ObjectTypeName? DisplayNameFor(string type) =>
        TiaPortalObjectTypeNames.TryFor(type);

    public MemberTable? TryParseMemberTable(string relativePath, string content) =>
        TiaPortalMemberTableParser.TryParse(relativePath, content);

    public Versio.Core.Iec.Ladder.LadderDocument? TryParseLadder(string relativePath, string content) =>
        Ladder.TiaPortalLadderParser.TryParse(relativePath, content);

    /// <summary>How to highlight this vendor's text files.</summary>
    /// <remarks>
    /// The files the import planner reads as text (<c>.scl</c>, <c>.s7dcl</c>, <c>.udt</c>) are
    /// Structured Text. Named here, by this vendor, since 2026-10-11; before, Core's language
    /// catalog and its tree each kept the list.
    /// </remarks>
    public Versio.Core.Diff.SyntaxLanguage? SyntaxFor(string relativePath) =>
        TiaPortalImportPlanner.NeedsText(relativePath) ? Versio.Core.Diff.LanguageCatalog.StructuredText : null;

    /// <summary>
    /// An SCL block's body: the lines after <c>BEGIN</c> up to the block's closing keyword
    /// (<c>END_FUNCTION_BLOCK</c>, <c>END_FUNCTION</c>, <c>END_ORGANIZATION_BLOCK</c>).
    /// </summary>
    /// <remarks>
    /// Only for <c>.scl</c>, and only lines that are the keyword alone - the word inside a
    /// comment or a string is not it. The closing keyword is the last such line, so an
    /// <c>END_FUNCTION</c> mentioned earlier cannot cut the body short. With no closing
    /// keyword the body runs to the end of the file rather than being refused.
    /// </remarks>
    public (int Start, int End)? CodeBody(string relativePath, string content)
    {
        if (!relativePath.EndsWith(".scl", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var begin = Array.FindIndex(lines, l => Is(l, "BEGIN"));
        if (begin < 0)
        {
            return null;
        }

        var end = Array.FindLastIndex(lines, l =>
            Is(l, "END_FUNCTION_BLOCK") || Is(l, "END_FUNCTION") || Is(l, "END_ORGANIZATION_BLOCK"));

        return (begin + 1, end > begin ? end : lines.Length);

        static bool Is(string line, string keyword) =>
            string.Equals(line.Trim(), keyword, StringComparison.OrdinalIgnoreCase);
    }
}
