using Versio.Core.Plc;
using Versio.Plc.Codesys;
using Versio.Core.Projects;
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

        /// <summary>
        /// A different word for a type string CODESYS also uses, and nothing for the rest.
        /// </summary>
        /// <remarks>
        /// <c>PLC_LOGIC</c> is chosen because CODESYS names it too, so this is the collision the
        /// split exists to make survivable: one manifest key, two vendors, two right answers.
        /// Returning null for everything else is what a real vendor mostly does — the standard
        /// already names <c>POU</c>, and repeating it here would rebuild the single table.
        /// </remarks>
        public Versio.Core.Projects.ObjectTypeName? DisplayNameFor(string type) =>
            string.Equals(type, "PLC_LOGIC", StringComparison.OrdinalIgnoreCase)
                ? new Versio.Core.Projects.ObjectTypeName("Runtime", "runtime", "runtimes")
                : null;
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

    [Fact]
    public void A_format_that_does_not_override_TryParseMemberTable_returns_null()
    {
        // Default-null is how a fourth vendor compiles without an App edit. The pane
        // asks every format; one that has nothing to table must not throw and must
        // not invent a table.
        IPlcProjectFormat format = new FakeTwinCatFormat();
        Assert.Null(format.TryParseMemberTable("block.xml", "<Document />"));
    }

    [Fact]
    public void A_format_that_does_not_override_TryParseLadder_returns_null()
    {
        // The same rule for ladder: a vendor with no ladder parser compiles and draws nothing,
        // and the pane falls through to the file's own view.
        IPlcProjectFormat format = new FakeTwinCatFormat();
        Assert.Null(format.TryParseLadder("block.xml", "<Document />"));
    }

    [Fact]
    public void One_type_string_renders_in_whichever_vendors_vocabulary_wrote_it()
    {
        // The collision the split exists to survive. PLC_LOGIC is a real CODESYS type and the
        // stand-in claims it too, with a different word - which is exactly what a second vendor
        // organising the same machine differently would do. Before the split there was one
        // table, so the second vendor to arrive would have had to accept the first one's word
        // or overwrite it for everybody.
        try
        {
            PlcFormats.Register(new FakeTwinCatFormat());

            Assert.Equal("PLC logic", ObjectTypeNames.Chip(PlcVendor.Codesys, "PLC_LOGIC"));
            Assert.Equal("Runtime", ObjectTypeNames.Chip(PlcVendor.TwinCat, "PLC_LOGIC"));
        }
        finally
        {
            PlcFormats.Unregister(PlcVendor.TwinCat);
        }
    }

    [Fact]
    public void A_vendor_that_names_nothing_still_gets_the_standards_words()
    {
        // The fall-through, and the reason it goes to IEC rather than straight to raw: POU,
        // ACTION and TRANSITION are the words a second vendor is most likely to share, so a
        // vendor implementing DisplayNameFor as "return null" renders them correctly for free.
        try
        {
            PlcFormats.Register(new FakeTwinCatFormat());

            Assert.Equal("POU", ObjectTypeNames.Chip(PlcVendor.TwinCat, "POU"));
            Assert.Equal("3 transitions", ObjectTypeNames.Counted(PlcVendor.TwinCat, "TRANSITION", 3));
        }
        finally
        {
            PlcFormats.Unregister(PlcVendor.TwinCat);
        }
    }

    [Fact]
    public void A_type_nobody_names_is_shown_exactly_as_the_exporter_wrote_it()
    {
        // Never fabricate. A build that met a type added after it shipped would otherwise
        // invent an English word for something it has never seen.
        Assert.Equal("QUANTUM_POU", ObjectTypeNames.Chip(PlcVendor.Codesys, "QUANTUM_POU"));
        Assert.Equal("1 QUANTUM_POU", ObjectTypeNames.Counted(PlcVendor.Codesys, "QUANTUM_POU", 1));
    }

    [Fact]
    public void The_two_halves_of_the_old_table_do_not_overlap()
    {
        // One key in both tables would make the rendering depend on lookup order, which is the
        // single-table problem wearing a different hat. METHOD is the near miss worth knowing
        // about: IEC names it, and CODESYS names METHOD_NORET separately.
        var shared = Versio.Core.Iec.IecObjectTypeNames.Known
            .Intersect(CodesysObjectTypeNames.Known, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(shared);
    }

    [Fact]
    public void The_CODESYS_log_category_still_reads_the_way_it_always_did()
    {
        // Pinned as a LITERAL, not against the constant, because comparing a constant to itself
        // proves nothing. The category moved out of LogCategory and into the vendor's own
        // namespace; the word it writes did not, and that is what keeps log files written by
        // older versions readable and the filter's saved toggles meaningful.
        Assert.Equal("Codesys", CodesysLog.Category);
    }
}
