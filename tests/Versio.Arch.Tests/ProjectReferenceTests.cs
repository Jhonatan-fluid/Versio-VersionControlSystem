using System.IO;
using System.Text.RegularExpressions;
using Versio.TestSupport;
using Xunit;

namespace Versio.Arch.Tests;

/// <summary>
/// What each project is <b>allowed to declare</b>, read from the <c>.csproj</c> files themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the metadata check turned out to be weaker than it claimed.</b>
/// <c>DependencyRuleTests</c> asks <c>Assembly.GetReferencedAssemblies()</c>, and the C# compiler
/// <i>drops references the code does not use</i> — so adding
/// <c>&lt;ProjectReference Include="Versio.Plc.Codesys"&gt;</c> to <c>Versio.Infrastructure</c>
/// and building left every one of those assertions green. Found by mutation, which is the only
/// reason it was found at all: the test passed, the claim was wrong.
/// </para>
/// <para>
/// The two checks answer different questions and both are worth asking. Metadata answers
/// <i>"does this assembly actually depend on that one"</i> — the real coupling. This answers
/// <i>"is the door open"</i>, which is what a reviewer reading the project file sees and what
/// somebody would add just before writing the code that uses it.
/// </para>
/// <para>
/// Source-reading is normally the wrong instrument here — the vendor seam scanner deliberately
/// reads IL rather than source so a brand in a comment is not a leak. A project file is
/// different: it <i>is</i> the declaration, there is nothing to interpret, and no compiler step
/// stands between what it says and what it means.
/// </para>
/// </remarks>
public sealed class ProjectReferenceTests
{
    private static readonly Regex Reference =
        new(@"<ProjectReference\s+Include=""(?<path>[^""]+)""", RegexOptions.Compiled);

    private static IReadOnlyList<string> ReferencesOf(string project)
    {
        var path = Path.Combine(TestPaths.Root, "src", project, project + ".csproj");
        Assert.True(File.Exists(path), $"{path} does not exist — the check would pass vacuously");

        return Reference.Matches(File.ReadAllText(path))
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups["path"].Value))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData("Versio.Core")]
    [InlineData("Versio.Infrastructure")]
    public void The_neutral_projects_declare_no_vendor_pack(string project)
    {
        // The architectural rule, as a build-time fact rather than an assertion about it.
        // Adding a vendor means a new project and one reference from Versio.App — never a
        // reference from here, however tempting it is while writing the code that would use it.
        var referenced = ReferencesOf(project);

        Assert.DoesNotContain("Versio.Plc.Codesys", referenced);
        Assert.DoesNotContain("Versio.Plc.Codesys.Infrastructure", referenced);
        Assert.DoesNotContain("Versio.Plc.TiaPortal", referenced);
        Assert.DoesNotContain("Versio.Plc.TiaPortal.Infrastructure", referenced);
        Assert.DoesNotContain("Versio.Plc.TiaPortal.Host", referenced);
    }

    [Fact]
    public void Versio_Core_declares_no_project_reference_at_all()
    {
        // It is the bottom of the graph. Anything it referenced would be something it could not
        // be tested without.
        Assert.Empty(ReferencesOf("Versio.Core"));
    }

    [Fact]
    public void Versio_App_is_the_only_place_a_vendor_is_named()
    {
        // The composition root, and the one project allowed to know which vendors this build
        // has. App.xaml.cs turns these references into Register() calls, one per pack.
        Assert.Contains("Versio.Plc.Codesys.Infrastructure", ReferencesOf("Versio.App"));
        Assert.Contains("Versio.Plc.TiaPortal.Infrastructure", ReferencesOf("Versio.App"));
    }

    [Fact]
    public void The_vendor_pack_depends_on_the_core_and_not_the_other_way_round()
    {
        // The direction that makes the whole split work. If this ever inverts, the compiler
        // reports a circular reference — but by then somebody has already restructured to get
        // there, and this says which direction was intended.
        Assert.Equal(["Versio.Core"], ReferencesOf("Versio.Plc.Codesys"));
        Assert.Equal(["Versio.Core"], ReferencesOf("Versio.Plc.TiaPortal"));
    }
}
