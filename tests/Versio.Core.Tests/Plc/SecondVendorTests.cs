using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// A second vendor, registered and recognised — the extension point exercised for the first time.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>PlcFormats.Register</c> had zero callers anywhere in the repository.</b> Not production,
/// not a test, not a sample. The architecture rests on it — the README, <c>CLAUDE.md</c> and the
/// registry's own docstring all say that adding a vendor means implementing the contracts and
/// registering — and the mechanism had never once been executed. The real composition was a
/// hardcoded array holding the one vendor that exists.
/// </para>
/// <para>
/// So this is not a test of a feature. It is the first evidence that the story the documentation
/// tells is true, and the first thing a second vendor would otherwise have discovered the hard
/// way.
/// </para>
/// <para>
/// <b>What it does NOT prove, despite an expectation that it would.</b> A stand-in vendor was
/// predicted to fail compiling, because <c>PlcVendor</c> is a closed enum in <c>Versio.Core</c>.
/// It compiles fine — the enum already declares TwinCAT, TIA Portal and Studio 5000 precisely so
/// the abstraction is judged against real second vendors. The closed-enum limitation is real but
/// it bites the <i>fifth</i> vendor, not the second, and nothing here demonstrates it.
/// </para>
/// <para>
/// <b>The fake claims extensions no real format does.</b> This registry is static and xunit runs
/// test classes in parallel by default, so for the moment the stand-in is registered every other
/// class sees it too. <c>.tcproj</c> collides with nothing, and <c>Unregister</c> — which had to
/// be written for this, because <c>Register</c> had no counterpart — puts the registry back.
/// Phase C removes the ambient static entirely, at which point this dance can go.
/// </para>
/// </remarks>
public sealed class SecondVendorTests
{
    /// <summary>A vendor Versio can see and cannot drive. Deliberately the useful case.</summary>
    private sealed class FakeTwinCatFormat : IPlcProjectFormat
    {
        public PlcVendor Vendor => PlcVendor.TwinCat;

        public string DisplayName => "TwinCAT (stand-in)";

        public PlcCapabilities Capabilities => new()
        {
            TextualExport = true,
            TextualImport = false,
            ObjectCreation = false,
            Headless = false,
            TypicalStartupCost = TimeSpan.FromSeconds(12),
        };

        public IReadOnlyList<string> ProjectExtensions => [".tcproj"];

        public bool IsTemplate(string path) =>
            path.EndsWith("_template.tcproj", StringComparison.OrdinalIgnoreCase);

        public string ConventionalExportPath(string projectPath) =>
            projectPath[..^".tcproj".Length];

        public string? OccupancyMarkerFor(string projectPath) => null;

        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];

        // This vendor records nothing, so every project reads as free.
        public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;

        public bool NeedsTextToPlan(string relativePath) => false;

        // An empty plan means "touch no file", which is the honest answer from a vendor that
        // declares TextualImport = false. Nothing here drives an import; the point of this
        // stand-in is that it can be SEEN, and a format is allowed to be registered without
        // being drivable.
        public ImportPlan PlanImport(
            Versio.Core.Projects.ProjectManifest? manifest,
            IReadOnlyList<MirrorFile> mirror,
            IReadOnlyDictionary<string, string> departed) => new() { Files = [] };
    }

    [Fact]
    public void A_registered_second_vendor_is_recognised_beside_the_first()
    {
        try
        {
            PlcFormats.Register(new FakeTwinCatFormat());

            // Both, not one. PlcFormats' own docstring names this as the case it exists for: a
            // repository holding a CODESYS project beside a TwinCAT one is two tracked projects
            // rather than one and a blind spot.
            Assert.Equal(PlcVendor.Codesys, PlcFormats.ForPath("plc/Machine.project")?.Vendor);
            Assert.Equal(PlcVendor.TwinCat, PlcFormats.ForPath("plc/Machine.tcproj")?.Vendor);

            Assert.Contains(".project", PlcFormats.AllExtensions);
            Assert.Contains(".tcproj", PlcFormats.AllExtensions);
        }
        finally
        {
            PlcFormats.Unregister(PlcVendor.TwinCat);
        }
    }

    [Fact]
    public void A_second_vendor_replaces_rather_than_duplicates_its_own_registration()
    {
        try
        {
            PlcFormats.Register(new FakeTwinCatFormat());
            PlcFormats.Register(new FakeTwinCatFormat());

            // Two formats claiming .tcproj would make ForPath answer whichever happened to be
            // registered first, which is the failure Register's remarks describe and nothing
            // had ever checked.
            Assert.Single(PlcFormats.All, f => f.Vendor == PlcVendor.TwinCat);
        }
        finally
        {
            PlcFormats.Unregister(PlcVendor.TwinCat);
        }
    }

    [Fact]
    public void Registering_a_vendor_does_not_disturb_the_one_already_there()
    {
        var before = PlcFormats.All.Count;

        try
        {
            PlcFormats.Register(new FakeTwinCatFormat());

            Assert.Equal(before + 1, PlcFormats.All.Count);
            Assert.Same(CodesysProjectFormat.Instance, PlcFormats.ForVendor(PlcVendor.Codesys));
        }
        finally
        {
            PlcFormats.Unregister(PlcVendor.TwinCat);
        }

        Assert.Equal(before, PlcFormats.All.Count);
    }

    [Fact]
    public void Unregistering_something_that_was_never_there_says_so_rather_than_throwing()
    {
        // A host tearing down its own registration should not have to remember whether it
        // succeeded in making it.
        Assert.False(PlcFormats.Unregister(PlcVendor.Studio5000));
    }
}
