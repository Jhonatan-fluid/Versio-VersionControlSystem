using Versio.Core.Bindings;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The vendor field, read from mirrors this repository actually commits.
/// </summary>
/// <remarks>
/// <para>
/// <c>ProjectManifestTests</c> proves the rules against hand-written JSON, which proves the
/// parser and nothing about the files. Everything read here is real exporter output, covering
/// both sides of the schema 8 boundary.
/// </para>
/// <para>
/// <b>The pre-schema-8 evidence is a preserved file now, not a live mirror.</b> Until
/// 2026-08-17 it was <c>MachineWithMembers</c>'s own manifest, kept at schema 7 deliberately,
/// and this file's test tripped — by design — when E2/E4 needed that fixture regenerated with
/// external-file objects. The choice the red test forced: the evidence is the <i>bytes</i>, not
/// the mirror, so the old manifest moved byte-identically to
/// <c>fixtures/manifests/MachineWithMembers-schema7-manifest.json</c> before the regeneration,
/// and the backward-compatible reading is still exercised against a file the exporter really
/// wrote before the vendor field existed. A fixture that is old on purpose is worth more than
/// one that is old by accident — but only if something says so, which is what this file is for,
/// and on 2026-08-17 it worked.
/// </para>
/// <para>
/// No CODESYS: everything here is committed. Milliseconds.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CommittedManifestVendorTests
{
    private static ProjectManifest Read(string exportDirectory) =>
        ProjectManifest.Parse(File.ReadAllText(Path.Combine(
            TestPaths.Root,
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
    public void A_manifest_written_before_schema_8_still_reads_as_CODESYS()
    {
        // Real evidence rather than a string in a test: this file was written by the exporter
        // before the vendor field existed, and it has to keep meaning what it meant. It lived
        // as MachineWithMembers' own manifest until 2026-08-17, when E2/E4 needed that fixture
        // regenerated - the schema<8 assertion below went red on the regeneration, exactly as
        // its comment promised, and the bytes moved here instead of being lost.
        var manifest = ProjectManifest.Parse(File.ReadAllText(Path.Combine(
            TestPaths.Root, "fixtures", "manifests", "MachineWithMembers-schema7-manifest.json")));

        Assert.True(manifest.Schema < 8, "this preserved file must never be rewritten by a "
            + "newer exporter - it exists to be old");
        Assert.Equal(PlcVendor.Codesys, manifest.Vendor);
    }
}
