using Versio.Core.Plc.Codesys;

namespace Versio.Core.Serialization;

/// <summary>How an object is written to disk. Mirrors <c>versio_types.py</c>.</summary>
public enum ExportForm
{
    /// <summary>Deliberately not exported (service objects, template-only state).</summary>
    Skip = 0,

    /// <summary>Structural node: becomes a directory, children walked.</summary>
    Container,

    /// <summary>Declaration + delimiter + implementation, one .st file.</summary>
    StFull,

    /// <summary>Declaration only (DUTs, GVL declarations).</summary>
    StDeclaration,

    /// <summary>Implementation only — actions and transitions have no declaration.</summary>
    StImplementation,

    /// <summary>Native .gvl.xml for structure plus .gvl.st for a readable diff.</summary>
    GvlBoth,

    /// <summary>Native export, non-recursive.</summary>
    Native,

    /// <summary>Native export, recursive — the object owns children we do not walk separately.</summary>
    NativeRecursive,

    /// <summary>
    /// Export the node itself natively (non-recursive) <b>and</b> descend into a
    /// subdirectory for its children.
    /// </summary>
    /// <remarks>
    /// Reserved for unmapped types that turn out to be structural. Probing a real project
    /// found two such nodes — "Plc Logic" and "Application" — that were Unknown before
    /// their GUIDs were added. Treating an unknown container as a plain leaf flattens its
    /// children into the parent's folder and quietly destroys the tree shape; treating it
    /// as a pure container would drop whatever payload the node itself carries. This does
    /// neither.
    /// </remarks>
    NativeWithChildren,
}

/// <summary>
/// The per-type export decision. Pure, table-driven and fully unit tested, because a
/// wrong answer here either loses graphical layout or produces an unreadable diff.
/// </summary>
public static class ExportFormRules
{
    private static readonly Dictionary<CodesysObjectType, ExportForm> Table = new()
    {
        [CodesysObjectType.Folder] = ExportForm.Container,
        [CodesysObjectType.Device] = ExportForm.Container,
        [CodesysObjectType.PlcLogic] = ExportForm.Container,
        [CodesysObjectType.Application] = ExportForm.Container,

        [CodesysObjectType.Pou] = ExportForm.StFull,
        [CodesysObjectType.Dut] = ExportForm.StDeclaration,
        [CodesysObjectType.Interface] = ExportForm.StFull,
        [CodesysObjectType.Gvl] = ExportForm.GvlBoth,
        [CodesysObjectType.GvlPersistent] = ExportForm.GvlBoth,
        [CodesysObjectType.Evc] = ExportForm.Native,
        [CodesysObjectType.Method] = ExportForm.StFull,
        [CodesysObjectType.MethodNoReturn] = ExportForm.StFull,
        [CodesysObjectType.Property] = ExportForm.NativeRecursive,
        [CodesysObjectType.Action] = ExportForm.StImplementation,
        [CodesysObjectType.Transition] = ExportForm.StImplementation,

        [CodesysObjectType.Visualisation] = ExportForm.Native,
        [CodesysObjectType.TaskConfiguration] = ExportForm.NativeRecursive,
        [CodesysObjectType.TextList] = ExportForm.Native,
        [CodesysObjectType.GlobalTextList] = ExportForm.Native,
        [CodesysObjectType.ImagePool] = ExportForm.Native,
        [CodesysObjectType.ProjectInformation] = ExportForm.Native,

        // Exporting the visualisation manager makes every later import raise an
        // interactive overwrite dialog (codescribe's finding). The template carries these.
        [CodesysObjectType.VisualisationManager] = ExportForm.Skip,
        [CodesysObjectType.TargetVisualisation] = ExportForm.Skip,
        [CodesysObjectType.WebVisualisation] = ExportForm.Skip,
        [CodesysObjectType.VisualisationStyle] = ExportForm.Skip,
        [CodesysObjectType.LibraryManager] = ExportForm.Skip,
        [CodesysObjectType.ProjectSettings] = ExportForm.Skip,
        [CodesysObjectType.Task] = ExportForm.Skip,
        [CodesysObjectType.CallToPou] = ExportForm.Skip,
    };

    /// <summary>
    /// Decide the on-disk form for one object.
    /// </summary>
    /// <remarks>
    /// The textual fallbacks matter: a POU declared in ST but drawn in CFC/LD/FBD has no
    /// textual implementation, and writing it as .st would silently discard its graphical
    /// layout. Those objects go to native XML instead.
    /// </remarks>
    public static ExportForm Decide(CodesysObjectType type, bool hasTextualImplementation,
                                    bool hasTextualDeclaration, bool hasChildren = false)
    {
        if (!Table.TryGetValue(type, out var form))
        {
            // Unmapped type (Unknown, or a type a newer CODESYS added).
            // Never silently drop it — native export preserves whatever it is.
            //
            // The type table is incomplete by nature: probing a 25-object toy project found
            // two missing GUIDs, so a real machine project will find more. An unknown node
            // WITH children is most likely structural, and must keep its subtree shape.
            return hasChildren ? ExportForm.NativeWithChildren : ExportForm.Native;
        }

        return form switch
        {
            ExportForm.StFull when !hasTextualImplementation => ExportForm.Native,
            ExportForm.StDeclaration when !hasTextualDeclaration => ExportForm.Native,
            ExportForm.StImplementation when !hasTextualImplementation => ExportForm.Native,
            ExportForm.GvlBoth when !hasTextualDeclaration => ExportForm.Native,
            _ => form,
        };
    }

    /// <summary>True when the form produces reviewable plain text rather than XML.</summary>
    public static bool IsTextual(ExportForm form) =>
        form is ExportForm.StFull or ExportForm.StDeclaration or ExportForm.StImplementation;
}
