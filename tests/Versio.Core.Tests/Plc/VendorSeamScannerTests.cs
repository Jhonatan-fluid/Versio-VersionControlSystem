using Versio.TestSupport;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// The positive control for the seam scanner: proof it can still find anything at all.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>VendorSeamTests</c> and <c>VendorSeamInfrastructureTests</c> both assert that a set is
/// empty.</b> A scanner that silently stopped working — a changed opcode constant, a namespace
/// filter that matches nothing, a reflection call that throws and is swallowed — satisfies every
/// one of those assertions perfectly, and the vendor seam would be unguarded with a green suite
/// reporting otherwise.
/// </para>
/// <para>
/// So each of the four things the scanner claims to find is demonstrated here against fixtures
/// written to be found. This is the standing version of the mutation checks run while the
/// scanner was written: those proved it worked once, on a machine, on a day, and were reverted.
/// </para>
/// </remarks>
public sealed class VendorSeamScannerTests
{
    private const string Fixtures = "Versio.Core.Tests.Plc.SeamFixtures";

    private static System.Reflection.Assembly Here => typeof(VendorSeamScannerTests).Assembly;

    [Fact]
    public void A_vendor_named_constant_is_found()
    {
        var found = VendorSeamScanner.VendorNamingConstants(Here, [Fixtures]).ToList();

        Assert.Contains(found, f => f.Contains("VendorNamingFixture.Category", StringComparison.Ordinal));
    }

    [Fact]
    public void A_vendor_named_literal_is_found()
    {
        var found = VendorSeamScanner.VendorNamingLiterals(Here, [Fixtures]).ToList();

        Assert.Contains(found, f => f.Contains("no CODESYS projects are tracked here either", StringComparison.Ordinal));
    }

    [Fact]
    public void A_vendor_named_literal_inside_a_lambda_is_found()
    {
        // The literal lives in a compiler-generated nested type, not in the method that reads
        // as its home. Finding it is what makes the scanner worth more than a grep for quotes.
        var found = VendorSeamScanner.VendorNamingLiterals(Here, [Fixtures]).ToList();

        Assert.Contains(found, f => f.Contains("a TwinCAT project would say this", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Formats")]
    [InlineData("AsArray")]
    [InlineData("ByName")]
    public void A_vendor_type_hiding_inside_a_generic_or_an_array_is_found(string member)
    {
        // One case per container shape, because they unwrap by different routes: a generic
        // argument, an array element, and a generic whose vendor type is the SECOND argument -
        // which a naive "first type argument" unwrap would miss.
        var found = VendorSeamScanner
            .VendorTypeReferences(Here, Fixtures, "Versio.Plc.Codesys")
            .ToList();

        Assert.Contains(found, f => f.Contains("CodesysProjectFormat", StringComparison.Ordinal));
        Assert.NotEmpty(found);
        Assert.True(
            typeof(SeamFixtures.GenericCarrier).GetProperty(member) is not null,
            $"the fixture no longer declares {member}, so this case proves nothing");
    }

    [Fact]
    public void Every_name_in_the_vendor_list_is_findable()
    {
        // The list itself is data, and a typo'd entry matches nothing anywhere, forever,
        // with every seam test green around it. EveryTell carries each entry verbatim, so
        // the entry that cannot be found here is the entry that guards nothing out there.
        // Added with the 2026-08-21 widening (Beckhoff, Openness, TcXae, "TIA Portal" with
        // its space), which landed as pure data with no proof any new entry worked.
        var found = VendorSeamScanner.VendorNamingLiterals(Here, [Fixtures]).ToList();

        foreach (var name in VendorSeamScanner.VendorNames)
        {
            Assert.Contains(found, f => f.Contains(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_namespace_with_nothing_in_it_finds_nothing()
    {
        // The other half of a positive control: it must be capable of an empty answer too, or
        // "empty" in the real seam tests would mean nothing either.
        Assert.Empty(VendorSeamScanner.VendorNamingLiterals(Here, ["Versio.Core.Tests.Nowhere"]));
        Assert.Empty(VendorSeamScanner.VendorNamingConstants(Here, ["Versio.Core.Tests.Nowhere"]));
    }

    [Fact]
    public void A_brand_in_a_comment_is_not_a_leak()
    {
        // The negative control. CommentOnlyFixture names four vendors in comments and XML docs
        // and none in a compiled string, which is the whole reason the scanner reads IL rather
        // than source: a comment is usually the clearest place to explain the vendor rule, and
        // flagging it would make the rule unwritable.
        //
        // It needs a namespace of its own. The first version of this test scanned this class
        // and filtered by name - which failed at once, because the tests above pass vendor
        // names as ASSERTION ARGUMENTS and every one of those is a real string literal. The
        // test was wrong on its own premise rather than on the scanner, and a negative control
        // that shares a namespace with deliberate leaks is not a control at all.
        var found = VendorSeamScanner
            .VendorNamingLiterals(Here, ["Versio.Core.Tests.Plc.CommentFixtures"])
            .Concat(VendorSeamScanner.VendorNamingConstants(Here, ["Versio.Core.Tests.Plc.CommentFixtures"]))
            .ToList();

        Assert.Empty(found);
    }
}
