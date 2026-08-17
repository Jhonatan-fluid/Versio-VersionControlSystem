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
