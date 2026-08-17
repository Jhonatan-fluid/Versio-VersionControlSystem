using System.Reflection;
using Versio.Core.Tests.Plc;
using Versio.Infrastructure.Git;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The vendor seam again, on the assembly nothing was checking.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule was only ever half-enforced.</b> <c>VendorSeamTests</c> reads
/// <c>typeof(PlcJob).Assembly</c> and stops there, so <c>Versio.Infrastructure</c> — which holds
/// the git client, the watchers, the filesystem and the toolchain registry — was never examined
/// at all. It happens to be clean today. Nothing was keeping it that way, and "it was clean when
/// somebody last looked" is exactly the state the original seam test was written to end.
/// </para>
/// <para>
/// <b>In this project rather than <c>Versio.Core.Tests</c></b> for the same reason
/// <c>VendorBoundaryTests</c> is: <c>Versio.Infrastructure</c> is where the code lives, and
/// <c>Versio.Core.Tests</c> deliberately does not reference it and should not start. It needs no
/// CODESYS, no python and no git, and runs in milliseconds — so it belongs in the fast lane, and
/// <c>FastLaneTests</c> asserts that by refusing to let it carry the toolchain tag.
/// </para>
/// <para>
/// The scanner itself is linked from <c>Versio.Core.Tests</c> rather than copied, the same
/// arrangement <c>TestPaths</c> already has. Two copies of a rule is how the two copies start
/// disagreeing.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class VendorSeamInfrastructureTests
{
    private static readonly Assembly Infrastructure = typeof(GitCliClient).Assembly;

    /// <summary>
    /// Namespaces in <c>Versio.Infrastructure</c> that must know nothing about any brand.
    /// </summary>
    /// <remarks>
    /// <c>Versio.Infrastructure.Plc</c> is absent for the same reason <c>Versio.Core.Plc</c> is
    /// absent from the list in <c>VendorSeamTests</c>: it is the seam itself, and
    /// <c>PlcToolchains</c> holds the factory that names the one vendor built so far. Phase C
    /// moves that to the application's composition root and this list should then grow by one.
    /// </remarks>
    private static readonly string[] NeutralNamespaces =
    [
        "Versio.Infrastructure.Bindings",
        "Versio.Infrastructure.FileSystem",
        "Versio.Infrastructure.Git",
        "Versio.Infrastructure.Logging",
        "Versio.Infrastructure.Platform",
        "Versio.Infrastructure.Watching",
    ];

    /// <summary>The vendor namespaces this assembly may reach into from its seam, and nowhere else.</summary>
    private static readonly string[] VendorNamespaces =
    [
        "Versio.Core.Plc.Codesys",
        "Versio.Infrastructure.Plc.Codesys",
    ];

    public static TheoryData<string> Neutral => [.. NeutralNamespaces];

    [Theory]
    [MemberData(nameof(Neutral))]
    public void A_neutral_namespace_names_no_vendor_in_its_own_types(string ns)
    {
        var offenders = VendorSeamScanner.VendorNamedTypes(Infrastructure, ns).ToList();

        Assert.True(offenders.Count == 0,
            $"{ns} declares vendor-named types: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(Neutral))]
    public void A_neutral_namespace_does_not_depend_on_a_vendor_type(string ns)
    {
        var offenders = VendorSeamScanner
            .VendorTypeReferences(Infrastructure, ns, VendorNamespaces)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "vendor knowledge has leaked past the seam:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void A_neutral_namespace_speaks_no_vendor_name_to_anybody()
    {
        // Empty, and asserted rather than assumed. Versio.Core's equivalent pins three known
        // leaks because it has three; this one has none, and the whole value of the test is that
        // the first one to arrive fails a build instead of shipping.
        var found = VendorSeamScanner
            .VendorNamingLiterals(Infrastructure, NeutralNamespaces)
            .Concat(VendorSeamScanner.VendorNamingConstants(Infrastructure, NeutralNamespaces))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(found);
    }
}
