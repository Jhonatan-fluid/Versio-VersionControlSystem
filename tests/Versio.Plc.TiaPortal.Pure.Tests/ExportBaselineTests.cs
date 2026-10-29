using Versio.Plc.TiaPortal.Host;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// The host's reading of the previous manifest, and the conditions under which it keeps an
/// object's files instead of asking Portal for them again. Every refusal is a full export of
/// that object, so each one is pinned on its own: a condition that quietly stopped being
/// checked would keep a stale file and nothing in the real gate would notice, because the
/// gate's project does not change between its two exports.
/// </summary>
public sealed class ExportBaselineTests
{
    private const string Portal = "TIA Portal V20 (Siemens.Engineering 20.0.0.0)";

    private static string Manifest(int schema = ExportBaseline.StampSchema, string vendor = "TiaPortal",
        string toolchain = Portal, string objects = DefaultObjects) =>
        $$"""
        {
          "schema": {{schema}},
          "vendor": "{{vendor}}",
          "toolchainVersion": "{{toolchain}}",
          "objects": [{{objects}}],
          "skipped": [],
          "warnings": []
        }
        """;

    private const string DefaultObjects = """
        { "guid": "Dev/PLC/FB/Pump#3", "name": "Pump", "type": "FB", "form": "SCL",
          "path": "Dev/PLC/Program blocks/Pump.scl", "hash": "sha256:text", "nhash": "", "stamp": "2026-10-29T20:00:00.0000000Z" },
        { "guid": "Dev/PLC/FB/Pump#3", "name": "Pump", "type": "FB", "form": "XML_REVIEW",
          "path": "Dev/PLC/Program blocks/Pump.xml", "hash": "sha256:raw", "nhash": "sha256:n", "stamp": "2026-10-29T20:00:00.0000000Z" }
        """;

    private static readonly string[] PumpPaths =
        ["Dev/PLC/Program blocks/Pump.xml", "Dev/PLC/Program blocks/Pump.scl"];

    /// <summary>Every recorded file is on disk with the bytes that were recorded.</summary>
    private static string? Intact(ExportBaseline.Record r) => r.Hash;

    [Fact]
    public void A_stamped_manifest_by_the_same_Portal_is_a_baseline()
    {
        var baseline = ExportBaseline.Load(Manifest(), Portal, out var refusal);

        Assert.NotNull(baseline);
        Assert.Equal("", refusal);
        Assert.Equal(1, baseline!.ObjectCount);
    }

    [Theory]
    [InlineData(null, "first export")]
    [InlineData("", "first export")]
    [InlineData("{ not json", "not readable JSON")]
    [InlineData("[]", "not a JSON object")]
    public void No_manifest_or_an_unreadable_one_is_a_full_export(string? json, string expected)
    {
        Assert.Null(ExportBaseline.Load(json, Portal, out var refusal));
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_below_the_stamp_schema_is_a_full_export()
    {
        // Schema 9 is what every TIA mirror exported before 2026-10-29 carries. It has no
        // stamps, and an empty stamp must never read as "unchanged".
        Assert.Null(ExportBaseline.Load(Manifest(schema: 9), Portal, out var refusal));
        Assert.Contains("schema 9", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_vendors_manifest_is_a_full_export()
    {
        Assert.Null(ExportBaseline.Load(Manifest(vendor: "Codesys"), Portal, out var refusal));
        Assert.Contains("'Codesys'", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_Portal_release_is_a_full_export()
    {
        // V21 generates different source text for the same object than V20 did; a stamp that
        // did not move says nothing about that.
        var refusal = "";
        Assert.Null(ExportBaseline.Load(Manifest(toolchain: "TIA Portal V21 (Siemens.Engineering.Base 21.0.0.0)"), Portal, out refusal));
        Assert.Contains("V21", refusal, StringComparison.Ordinal);
        Assert.Contains("V20", refusal, StringComparison.Ordinal);

        Assert.Null(ExportBaseline.Load(Manifest(toolchain: ""), Portal, out refusal));
        Assert.Contains("names no toolchain", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_with_the_same_stamp_and_intact_files_is_kept_with_its_records()
    {
        var baseline = ExportBaseline.Load(Manifest(), Portal, out _)!;

        var kept = baseline.Keep("Dev/PLC/FB/Pump#3", "2026-10-29T20:00:00.0000000Z", PumpPaths, Intact, out var reason);

        Assert.Equal(ExportBaseline.ExportReason.Kept, reason);
        Assert.NotNull(kept);
        Assert.Equal(2, kept!.Count);
        Assert.Equal("sha256:text", kept[0].Hash);
        Assert.Equal("sha256:n", kept[1].NHash);
        Assert.True(kept[0].IsTextual);
        Assert.False(kept[1].IsTextual);
    }

    [Fact]
    public void A_moved_stamp_exports()
    {
        var baseline = ExportBaseline.Load(Manifest(), Portal, out _)!;

        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump#3", "2026-10-29T20:00:01.0000000Z", PumpPaths, Intact, out var reason));
        Assert.Equal(ExportBaseline.ExportReason.Changed, reason);
    }

    [Fact]
    public void An_object_without_a_stamp_exports()
    {
        var baseline = ExportBaseline.Load(Manifest(), Portal, out _)!;

        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump#3", "", PumpPaths, Intact, out var reason));
        Assert.Equal(ExportBaseline.ExportReason.NoStamp, reason);
    }

    [Fact]
    public void An_identity_the_last_export_did_not_record_exports()
    {
        // New, renamed (the name is in the identity), or skipped last time with a failure —
        // all three have no record and all three must be asked of Portal.
        var baseline = ExportBaseline.Load(Manifest(), Portal, out _)!;

        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump_2#3", "2026-10-29T20:00:00.0000000Z",
            ["Dev/PLC/Program blocks/Pump_2.xml", "Dev/PLC/Program blocks/Pump_2.scl"], Intact, out var reason));
        Assert.Equal(ExportBaseline.ExportReason.New, reason);
    }

    [Fact]
    public void An_object_recorded_at_another_place_in_the_tree_exports()
    {
        // Moved to another group in Portal: identity and stamp may both survive, and the
        // files recorded under the old folder are not the files this walk writes.
        var baseline = ExportBaseline.Load(Manifest(), Portal, out _)!;

        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump#3", "2026-10-29T20:00:00.0000000Z",
            ["Dev/PLC/Program blocks/Pumps/Pump.xml", "Dev/PLC/Program blocks/Pumps/Pump.scl"], Intact, out var reason));
        Assert.Equal(ExportBaseline.ExportReason.Moved, reason);
    }

    [Fact]
    public void A_recorded_file_that_is_missing_or_edited_exports()
    {
        var baseline = ExportBaseline.Load(Manifest(), Portal, out _)!;

        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump#3", "2026-10-29T20:00:00.0000000Z", PumpPaths,
            r => r.IsTextual ? null : r.Hash, out var missing));
        Assert.Equal(ExportBaseline.ExportReason.Unverifiable, missing);

        // Somebody edited the mirror's .scl since the export: whatever the stamp says, the
        // file on disk is not the last export, and keeping it would record the edit as
        // Portal's truth.
        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump#3", "2026-10-29T20:00:00.0000000Z", PumpPaths,
            r => r.IsTextual ? "sha256:edited" : r.Hash, out var edited));
        Assert.Equal(ExportBaseline.ExportReason.Unverifiable, edited);
    }

    [Fact]
    public void A_record_whose_files_disagree_on_the_stamp_exports()
    {
        // Two records under one identity with two stamps cannot both be the last export of
        // the object; the only safe reading is that neither is.
        var objects = DefaultObjects.Replace(
            "\"hash\": \"sha256:raw\", \"nhash\": \"sha256:n\", \"stamp\": \"2026-10-29T20:00:00.0000000Z\"",
            "\"hash\": \"sha256:raw\", \"nhash\": \"sha256:n\", \"stamp\": \"2026-10-29T19:00:00.0000000Z\"",
            StringComparison.Ordinal);
        var baseline = ExportBaseline.Load(Manifest(objects: objects), Portal, out _)!;

        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump#3", "2026-10-29T20:00:00.0000000Z", PumpPaths, Intact, out var reason));
        Assert.Equal(ExportBaseline.ExportReason.Changed, reason);
    }

    [Fact]
    public void A_record_without_a_hash_cannot_be_verified_and_exports()
    {
        var objects = DefaultObjects.Replace("\"hash\": \"sha256:text\"", "\"hash\": \"\"", StringComparison.Ordinal);
        var baseline = ExportBaseline.Load(Manifest(objects: objects), Portal, out _)!;

        Assert.Null(baseline.Keep("Dev/PLC/FB/Pump#3", "2026-10-29T20:00:00.0000000Z", PumpPaths, Intact, out var reason));
        Assert.Equal(ExportBaseline.ExportReason.Unverifiable, reason);
    }
}
