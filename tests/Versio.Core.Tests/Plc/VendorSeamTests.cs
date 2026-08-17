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

    /// <summary>
    /// The vendor-naming strings in neutral namespaces today, pinned so they cannot grow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A list that exists to be deleted.</b> These three are the leaks the string checks were
    /// written to find, and the next commit removes them. They are pinned rather than tolerated
    /// so that this file passes on its own — a red commit is a commit somebody cannot bisect
    /// through — while a <i>fourth</i> leak still fails the build the day it is typed.
    /// </para>
    /// <para>
    /// Two of them are sentences a person reads. The commit gate tells somebody whose repository
    /// holds only TwinCAT projects that "no CODESYS projects are tracked", which is not a
    /// cosmetic problem: it is the application confidently describing a situation that is not
    /// the one they are in.
    /// </para>
    /// </remarks>
    private static readonly string[] KnownLeaks =
    [
        "CommitGate.Evaluate: \"no CODESYS projects are tracked in this repository\"",
        "OrphanScanner.Scan: \"untracked CODESYS project — track it to start exporting\"",
        "Versio.Core.Logging.LogCategory.Codesys = \"Codesys\"",
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

    [Fact]
    public void A_neutral_namespace_speaks_no_vendor_name_to_anybody()
    {
        // Constants and compiled literals together, because the two halves of this leak are the
        // same mistake wearing different clothes: a brand written down somewhere that serves
        // every brand. Sorted and compared as a whole set rather than asserted empty, so that
        // removing one of the three is a visible deletion here rather than a silent loosening.
        var found = VendorSeamScanner
            .VendorNamingLiterals(Core, NeutralNamespaces)
            .Concat(VendorSeamScanner.VendorNamingConstants(Core, NeutralNamespaces))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            KnownLeaks.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            found);
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
