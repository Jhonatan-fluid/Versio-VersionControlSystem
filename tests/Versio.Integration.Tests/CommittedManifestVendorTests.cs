using Versio.Core.Bindings;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The vendor field, read from mirrors this repository actually commits.
/// </summary>
/// <remarks>
/// <para>
/// <c>ProjectManifestTests</c> proves the rules against hand-written JSON, which proves the
/// parser and nothing about the files. These two fixtures are real exporter output, and between
/// them they happen to cover both sides of the schema 8 boundary:
/// </para>
/// <list type="bullet">
/// <item><c>RefrigeratorControl</c> was regenerated when the field was added, so it is schema 8
/// and names its vendor.</item>
/// <item><c>MachineWithMembers</c> was not, so it is still schema 7 with no vendor at all —
/// which makes it the only committed artifact in the repository that exercises the
/// backward-compatible reading against a file nobody wrote for the purpose.</item>
/// </list>
/// <para>
/// <b>That second one is kept at schema 7 deliberately.</b> Nothing asserts its schema, so
/// regenerating it would have been free and would have quietly destroyed the only real
/// pre-schema-8 evidence in the tree. A fixture that is old on purpose is worth more than one
/// that is old by accident — but only if something says so, which is what this file is for.
/// </para>
/// <para>
/// No CODESYS: both manifests are committed. Milliseconds.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CommittedManifestVendorTests
{
    private static ProjectManifest Read(string exportDirectory) =>
        ProjectManifest.Parse(File.ReadAllText(Path.Combine(
            Core.Tests.TestPaths.Root,
            "fixtures", "projects", exportDirectory,
            BindingConventions.ManifestFileName)));

    [Fact]
    public void The_regenerated_mirror_names_the_vendor_that_wrote_it()
    {
        var manifest = Read("RefrigeratorControl");

        Assert.Equal(ExportCoverage.CurrentSchema, manifest.Schema);
        Assert.Equal(PlcVendor.Codesys, manifest.Vendor);
    }

    [Fact]
    public void A_mirror_committed_before_schema_8_still_reads_as_CODESYS()
    {
        // Real evidence rather than a string in a test: this file was written by the exporter
        // before the field existed, and it has to keep meaning what it meant. If somebody
        // regenerates it, this test goes red and the loss is a decision rather than a side
        // effect - which is exactly what happened to the assertion in
        // WorkspaceTreeFromCommittedExportTests when its own mirror went stale.
        var manifest = Read("MachineWithMembers");

        Assert.True(manifest.Schema < 8, "the fixture was regenerated; it was the last pre-8 one");
        Assert.Equal(PlcVendor.Codesys, manifest.Vendor);
    }
}
