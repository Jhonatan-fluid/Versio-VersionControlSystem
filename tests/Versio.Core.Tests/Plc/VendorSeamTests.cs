using System.Reflection;
using Versio.Core.Plc;
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
    /// <c>Versio.Core.Plc</c> is deliberately absent: it is the seam itself, and
    /// <c>PlcFormats</c> holds the registry that names the one vendor built so far. Phase C of
    /// the restructure moves that registration out to the application's composition root, at
    /// which point this list should grow by one.
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
    ];

    public static TheoryData<string> Neutral => [.. NeutralNamespaces];

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
            .VendorTypeReferences(Core, ns, "Versio.Core.Plc.")
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
        // PlcVendor lists brands that do not exist yet on purpose - the abstraction is judged
        // against real second vendors rather than a hypothetical one. This asserts the list is
        // still there, so nobody "tidies" it down to the one that works.
        var vendors = Enum.GetNames<PlcVendor>();

        Assert.Contains("Codesys", vendors);
        Assert.Contains("TiaPortal", vendors);
        Assert.Contains("Studio5000", vendors);
        Assert.Contains("TwinCat", vendors);
    }
}
