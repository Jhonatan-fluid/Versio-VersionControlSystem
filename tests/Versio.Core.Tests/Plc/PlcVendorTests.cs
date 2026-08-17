using System.Text.Json;
using Versio.Core.Plc;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// The vendor identity, after it stopped being a closed enum.
/// </summary>
/// <remarks>
/// Every one of these is a property the enum had, or a hazard the enum carried. They are pinned
/// together because the change is only safe if all of them hold at once: a committed
/// <c>repo.json</c> has to keep meaning what it meant, and a binding has to keep resolving to
/// the toolchain it always resolved to.
/// </remarks>
public sealed class PlcVendorTests
{
    [Fact]
    public void A_vendor_nobody_has_heard_of_can_still_be_named()
    {
        // The whole point. While this was an enum, a vendor that was not already listed could
        // not be named at all without editing Versio.Core - the one assembly the architectural
        // rule exists to keep untouched. You cannot register what you cannot name.
        var exotic = new PlcVendor("Mitsubishi GX Works3");

        Assert.Equal("Mitsubishi GX Works3", exotic.Name);
        Assert.DoesNotContain(exotic, PlcVendor.Known);
    }

    [Fact]
    public void The_four_well_known_vendors_survive()
    {
        // Known is a shipped list, not a limit. Three of these are unimplemented and cost
        // nothing; they are how the abstraction gets judged against real second vendors.
        Assert.Contains(PlcVendor.Codesys, PlcVendor.Known);
        Assert.Contains(PlcVendor.TiaPortal, PlcVendor.Known);
        Assert.Contains(PlcVendor.Studio5000, PlcVendor.Known);
        Assert.Contains(PlcVendor.TwinCat, PlcVendor.Known);
    }

    [Theory]
    [InlineData("Codesys")]
    [InlineData("CODESYS")]
    [InlineData("codesys")]
    public void Spelling_does_not_decide_which_toolchain_a_binding_gets(string spelling)
    {
        // repo.json is committed and hand-editable. A binding that stopped matching its
        // toolchain over the case of one letter is a very confusing way to lose an export.
        Assert.Equal(PlcVendor.Codesys, new PlcVendor(spelling));
    }

    [Fact]
    public void An_unset_vendor_does_not_silently_claim_one()
    {
        // As an enum, default was ordinal 0 - which was Codesys. An uninitialised field
        // therefore claimed a vendor rather than admitting it had none.
        var unset = default(PlcVendor);

        Assert.True(unset.IsUnknown);
        Assert.NotEqual(PlcVendor.Codesys, unset);
        Assert.Equal(string.Empty, unset.Name);
    }

    [Fact]
    public void A_vendor_serialises_as_the_plain_string_repo_json_already_holds()
    {
        // The bytes must not move. Every committed repo.json in every clone holds
        // "vendor": "Codesys", written when this was an enum with JsonStringEnumConverter.
        var json = JsonSerializer.Serialize(PlcVendor.Codesys);

        Assert.Equal("\"Codesys\"", json);
        Assert.Equal(PlcVendor.Codesys, JsonSerializer.Deserialize<PlcVendor>(json));
    }

    [Fact]
    public void A_vendor_read_from_an_older_file_still_resolves()
    {
        // The shape a binding is actually read in, rather than the value on its own.
        var binding = JsonSerializer.Deserialize<VendorHolder>("""{"vendor":"Codesys"}""");

        Assert.Equal(PlcVendor.Codesys, binding!.Vendor);
    }

    [Fact]
    public void A_vendor_works_as_a_dictionary_key()
    {
        // PlcToolchains keys its factories by vendor, so equality and hashing have to agree.
        // A struct with reference equality here would look like a registry that forgets.
        var map = new Dictionary<PlcVendor, string>
        {
            [PlcVendor.Codesys] = "one",
            [new PlcVendor("Mitsubishi")] = "two",
        };

        Assert.Equal("one", map[new PlcVendor("CODESYS")]);
        Assert.Equal("two", map[new PlcVendor("mitsubishi")]);
    }

    [Fact]
    public void A_vendor_used_as_a_json_dictionary_key_stays_a_plain_string()
    {
        // WriteAsPropertyName and ReadAsPropertyName exist because JSON object keys must be
        // strings, and a JsonConverter without them throws the moment somebody serialises a map
        // keyed by vendor. Nothing does that today - so without this test they would be
        // speculative code with no evidence they work, which is worse than not having them.
        var json = JsonSerializer.Serialize(
            new Dictionary<PlcVendor, int> { [PlcVendor.Codesys] = 1 });

        Assert.Equal("{\"Codesys\":1}", json);

        var back = JsonSerializer.Deserialize<Dictionary<PlcVendor, int>>(json);

        Assert.Equal(1, back![PlcVendor.Codesys]);
    }

    [Fact]
    public void A_vendor_interpolated_into_a_log_line_reads_as_its_name()
    {
        Assert.Equal("driving Codesys", $"driving {PlcVendor.Codesys}");
    }

    private sealed class VendorHolder
    {
        [System.Text.Json.Serialization.JsonPropertyName("vendor")]
        public PlcVendor Vendor { get; init; }
    }
}
