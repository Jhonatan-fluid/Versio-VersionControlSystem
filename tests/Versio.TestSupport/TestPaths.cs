namespace Versio.TestSupport;

/// <summary>
/// Locates the repository root from the test assembly's own location.
/// </summary>
/// <remarks>
/// Fixture paths were originally hardcoded to <c>C:\VERSIO</c>, and every one of them broke
/// the moment the checkout moved. Walking up to the marker file means the suite runs from
/// any clone path, on any machine, and in CI — which is the whole point of committing the
/// fixtures in the first place.
/// </remarks>
public static class TestPaths
{
    private static readonly Lazy<string> RootValue = new(FindRoot);

    /// <summary>Repository root.</summary>
    public static string Root => RootValue.Value;

    public static string Fixtures => Path.Combine(Root, "fixtures");

    public static string Equivalence(string side, string file) =>
        Path.Combine(Fixtures, "equivalence", side, file);

    public static string Project(string name) =>
        Path.Combine(Fixtures, "projects", name);

    /// <summary>
    /// The committed TIA Portal gate fixture, or null when this clone has LFS pointers
    /// instead of a real project.
    /// </summary>
    /// <remarks>
    /// The tree lives at <c>fixtures/projects/CleaningStation_V20_V21/</c>. A clone without
    /// <c>git lfs pull</c> gets a 130-byte pointer for the <c>.ap21</c> and an empty
    /// <c>System/</c> — TIA cannot open that, and the gate skips rather than going red.
    /// The original OneDrive copy is never this path.
    /// </remarks>
    public static string? TiaPortalProject()
    {
        var dir = Path.Combine(Fixtures, "projects", "CleaningStation_V20_V21");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var ap = Directory.EnumerateFiles(dir, "*.ap21").FirstOrDefault();
        if (ap is null)
        {
            return null;
        }

        // An LFS pointer is ~130 bytes; the real pointer file is ~151 KB.
        if (new FileInfo(ap).Length < 1024)
        {
            return null;
        }

        var system = Path.Combine(dir, "System");
        if (!Directory.Exists(system)
            || !Directory.EnumerateFiles(system).Any())
        {
            return null;
        }

        return ap;
    }

    /// <summary>
    /// The committed TwinCAT gate fixture, or null when the <c>.tsproj</c> is missing.
    /// </summary>
    /// <remarks>
    /// The tree lives at <c>fixtures/projects/TC3_PlcSample_reStrText/</c>. No LFS — this is
    /// XML plus a few images. Work only on a <b>copy</b> under %TEMP%; do not adopt it into
    /// this repository's own bindings.
    /// </remarks>
    public static string? TwinCatProject()
    {
        var dir = Path.Combine(Fixtures, "projects", "TC3_PlcSample_reStrText");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var tsproj = Directory.EnumerateFiles(dir, "*.tsproj").FirstOrDefault();
        if (tsproj is null)
        {
            return null;
        }

        var sln = Path.ChangeExtension(tsproj, ".sln");
        if (!File.Exists(sln))
        {
            return null;
        }

        return tsproj;
    }

    /// <summary>
    /// The python, on disk rather than embedded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parity tests read the SOURCE, deliberately: their subject is the script a person
    /// edits, and comparing the embedded copy would only ever prove the last build agreed with
    /// itself.
    /// </para>
    /// <para>
    /// <b>Here rather than spelled out at each call site.</b> Three tests hardcoded
    /// <c>src/Versio.Scripts.Codesys</c>, and all three broke together when the scripts moved
    /// into the vendor pack — the same reason this class exists at all, one directory along.
    /// </para>
    /// </remarks>
    public static string Scripts =>
        Path.Combine(Root, "src", "Versio.Plc.Codesys.Infrastructure", "Scripts");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        // Versio.sln is the unambiguous marker: a "fixtures" folder could plausibly exist
        // elsewhere, and .git is absent in an exported archive.
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Versio.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"could not find Versio.sln above {AppContext.BaseDirectory}");
    }
}
