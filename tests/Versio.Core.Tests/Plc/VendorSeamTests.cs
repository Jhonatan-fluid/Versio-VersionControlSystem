using System.Reflection;
using Versio.Core.Plc;
using Versio.TestSupport;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// The vendor seam, enforced rather than documented.
/// </summary>
/// <remarks>
/// <para>
/// Versio started CODESYS-only and will grow to Rockwell, Siemens and TwinCAT. The README has
/// always said that adding a vendor must not require changing <c>Sync/</c>, <c>Git/</c>,
/// <c>Locking/</c>, <c>Watching/</c> or <c>Diff/</c> — and it was not true: the sync
/// orchestrator was built directly on <c>ICodesysRunner</c>, <c>CodesysJob</c> and
/// <c>CodesysResult</c>, so a second vendor would have meant rewriting the engine.
/// </para>
/// <para>
/// A rule nothing checks is a rule that decays. These tests read the assembly's own metadata,
/// so the next leak fails a build rather than surviving until somebody happens to grep.
/// </para>
/// <para>
/// <b>Checking signatures was not enough, and the proof is that it passed.</b> Every leak this
/// file could see had been fixed, and three were sitting in the tree the whole time: two
/// sentences shown to a person and one constant in a table serving every vendor. They survived
/// because a string is not a signature. <see cref="VendorSeamScanner"/> now reads constants and
/// compiled string literals as well, and <c>Versio.Infrastructure</c> is checked by
/// <c>VendorSeamInfrastructureTests</c> — until this session nothing looked at that assembly at
/// all.
/// </para>
/// </remarks>
public sealed class VendorSeamTests
{
    private static readonly Assembly Core = typeof(PlcJob).Assembly;

    /// <summary>Namespaces that must know nothing about any particular brand.</summary>
    /// <remarks>
    /// <c>Versio.Core.Plc</c> is absent from THIS list and checked separately below. It is the
    /// seam itself, and it names the four well-known vendors on purpose — so "contains no brand
    /// name" is the wrong assertion for it, and "contains exactly these four" is the right one.
    /// Everything else about it is held to the same rules as the rest.
    /// </remarks>
    private static readonly string[] NeutralNamespaces =
    [
        "Versio.Core.Sync",
        "Versio.Core.Git",
        "Versio.Core.Locking",
        "Versio.Core.Watching",
        "Versio.Core.Diff",
        "Versio.Core.Bindings",
        "Versio.Core.Projects",
        "Versio.Core.Abstractions",
        "Versio.Core.Hashing",
        "Versio.Core.Logging",
        "Versio.Core.Guarding",
        "Versio.Core.Iec",
        "Versio.Core.Settings",
    ];

    /// <summary>
    /// The seam. Held to the type rules with everything else, from 2026-08-17.
    /// </summary>
    /// <remarks>
    /// It could not be, until the vendor moved into its own assembly: <c>PlcFormats</c> held
    /// <c>[CodesysProjectFormat.Instance]</c> in a field initialiser, so the one namespace whose
    /// job is to define the seam was the one namespace that crossed it.
    /// </remarks>
    private const string Seam = "Versio.Core.Plc";

    public static TheoryData<string> Neutral => [.. NeutralNamespaces, Seam];

    [Theory]
    [MemberData(nameof(Neutral))]
    public void A_neutral_namespace_names_no_vendor_in_its_own_types(string ns)
    {
        var offenders = VendorSeamScanner.VendorNamedTypes(Core, ns).ToList();

        Assert.True(offenders.Count == 0,
            $"{ns} declares vendor-named types: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(Neutral))]
    public void A_neutral_namespace_does_not_depend_on_a_vendor_type(string ns)
    {
        var offenders = VendorSeamScanner
            .VendorTypeReferences(Core, ns, "Versio.Plc.")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "vendor knowledge has leaked past the seam:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>No brand written down anywhere that serves every brand.</summary>
    /// <remarks>
    /// <para>
    /// Constants and compiled literals together, because the two halves are the same mistake
    /// wearing different clothes. When this check was added it found three, and they had all
    /// been there long enough to be invisible:
    /// </para>
    /// <para>
    /// <c>CommitGate</c> told somebody "no CODESYS projects are tracked in this repository" —
    /// the application confidently describing a situation that is not the one a TwinCAT owner is
    /// in. <c>OrphanScanner</c> offered to track an "untracked CODESYS project". And
    /// <c>LogCategory</c> carried a <c>Codesys</c> constant directly beneath the paragraph
    /// explaining why the category above it must never name a vendor.
    /// </para>
    /// <para>
    /// The first two are now "PLC"; the third moved to <c>CodesysLog.Category</c>, where the
    /// vendor declares its own label and the logged word is unchanged.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_neutral_namespace_speaks_no_vendor_name_to_anybody()
    {
        var found = VendorSeamScanner
            .VendorNamingLiterals(Core, NeutralNamespaces)
            .Concat(VendorSeamScanner.VendorNamingConstants(Core, NeutralNamespaces))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void The_seam_names_the_four_well_known_vendors_and_no_others()
    {
        // Versio.Core.Plc is the one place in this assembly allowed to write a brand down, and
        // PlcVendor's well-known statics are why. So the assertion is not "no brands" - it is
        // "exactly these brands", which fails just as loudly on a fifth one appearing as the
        // neutral rule does on a first.
        //
        // A fifth vendor belongs in its own pack, named by whoever registers it. Adding a static
        // here would put it back in the assembly the whole rule exists to keep clean.
        var brands = VendorSeamScanner
            .VendorNamingLiterals(Core, [Seam])
            .Concat(VendorSeamScanner.VendorNamingConstants(Core, [Seam]))
            .SelectMany(found => VendorSeamScanner.VendorNames.Where(
                v => found.Contains(v, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Codesys", "Studio5000", "TiaPortal", "TwinCat"], brands);
    }

    [Fact]
    public void No_neutral_namespace_can_even_see_the_vendor_pack()
    {
        // The compiler enforces this now, and this test says so out loud: Versio.Core has no
        // project reference to Versio.Plc.Codesys, so a leak is a build failure rather than a
        // reflection test's opinion. If this ever passes trivially because somebody added the
        // reference back, the assertion below is what notices.
        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("Versio.Plc.Codesys", referenced);
        Assert.DoesNotContain("Versio.Plc.Codesys.Infrastructure", referenced);
        Assert.DoesNotContain("Versio.Infrastructure", referenced);
    }

    [Fact]
    public void The_neutral_toolchain_contract_is_what_a_new_vendor_implements()
    {
        // Named explicitly so that deleting or renaming one of these is a deliberate act
        // rather than something that happens during a refactor.
        Assert.NotNull(Core.GetType("Versio.Core.Plc.IPlcRunner"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.IPlcProjectFormat"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.IPlcToolchain"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.PlcJob"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.PlcResult"));
    }

    [Fact]
    public void Every_declared_vendor_is_either_implemented_or_honestly_absent()
    {
        // PlcVendor.Known lists brands that do not exist yet on purpose - the abstraction is
        // judged against real second vendors rather than a hypothetical one. This asserts the
        // list is still there, so nobody "tidies" it down to the one that works.
        //
        // Known is a well-known list, NOT a limit: PlcVendor stopped being an enum so that a
        // vendor nobody has heard of can still be named and registered without editing
        // Versio.Core. That is asserted separately, below.
        var vendors = PlcVendor.Known.Select(v => v.Name).ToList();

        Assert.Contains("Codesys", vendors);
        Assert.Contains("TiaPortal", vendors);
        Assert.Contains("Studio5000", vendors);
        Assert.Contains("TwinCat", vendors);
    }

    [Fact]
    public void A_vendor_outside_the_known_list_can_still_be_named()
    {
        // The half that matters to the architectural rule, and the half a closed enum could not
        // do. Adding a vendor must not require editing Versio.Core, and naming one is the first
        // thing adding one requires - PlcFormats and PlcToolchains are both keyed by this.
        var exotic = new PlcVendor("Mitsubishi GX Works3");

        Assert.False(exotic.IsUnknown);
        Assert.DoesNotContain(exotic, PlcVendor.Known);
    }
}
