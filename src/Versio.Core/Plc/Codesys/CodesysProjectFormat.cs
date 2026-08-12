using Versio.Core.Bindings;

namespace Versio.Core.Plc.Codesys;

/// <summary>
/// CODESYS 3.5's project-file conventions.
/// </summary>
/// <remarks>
/// The first concrete <see cref="IPlcProjectFormat"/>, and the reference for what a second
/// vendor has to supply. Everything here is genuinely CODESYS-specific: the extensions, the
/// template naming convention, and what the toolchain can do.
/// </remarks>
public sealed class CodesysProjectFormat : IPlcProjectFormat
{
    public static readonly CodesysProjectFormat Instance = new();

    public PlcVendor Vendor => PlcVendor.Codesys;

    public string DisplayName => "CODESYS 3.5";

    /// <remarks>
    /// Every capability here was measured against a real project rather than assumed —
    /// see <c>docs/PHASE0-FINDINGS.md</c>. <see cref="PlcCapabilities.TypicalStartupCost"/>
    /// in particular is why export-on-save is not the default sync mode.
    /// </remarks>
    public PlcCapabilities Capabilities => new()
    {
        TextualExport = true,
        TextualImport = true,
        ObjectCreation = true,
        Headless = true,
        TypicalStartupCost = TimeSpan.FromSeconds(20),
    };

    /// <remarks>
    /// <c>.library</c> is included because a library project is still a CODESYS project;
    /// importing one is not yet supported, but it must be <i>seen</i> so the user is told
    /// that rather than left wondering why it was ignored.
    /// </remarks>
    public IReadOnlyList<string> ProjectExtensions => [".project", ".library"];

    public bool IsTemplate(string path) => BindingConventions.LooksLikeTemplate(path);

    public string ConventionalExportPath(string projectPath) =>
        BindingConventions.ConventionalExportPath(projectPath);
}
