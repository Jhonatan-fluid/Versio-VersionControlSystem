using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// Reading <c>_manifest.json</c>: the file the exporter writes and the panel draws from.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because the two halves drifted and nothing noticed for the length of a
/// feature. <c>versio_export.py</c> has always written <c>warnings</c> as records —
/// <c>{"code": …, "path": …, "detail": …}</c> — and the C# model declared them as strings, so
/// the first project to produce a single warning failed to deserialize <b>entirely</b>: 56
/// objects discarded, the tree drawn as a flat list of raw paths, and coverage reported as
/// unknown. Every existing test used <c>"warnings": []</c>, which is valid for both shapes.
/// </para>
/// <para>
/// So the rule these hold to is not "parse the shape we happen to write today" but <b>one
/// unreadable field costs that field alone</b>. A manifest is a convenience — the export tree
/// on disk is the truth — and throwing away 56 objects because a seventh field disagreed is
/// the worst available reading of it.
/// </para>
/// </remarks>
public sealed class ProjectManifestTests
{
    /// <summary>The shape a real export writes, warnings and all.</summary>
    /// <remarks>
    /// Copied from the manifest of <c>ObjectOrientedProgramming(Extended).project</c>, the
    /// project that surfaced this — an object CODESYS knows as an external file reference,
    /// whose type GUID the exporter does not recognise, which is what raises the warning.
    /// </remarks>
    private const string RealManifest = """
    {
      "schema": 3,
      "bindingId": "d4e33864d816",
      "project": "ObjectOrientedProgramming(Extended).project",
      "objectCount": 2,
      "objects": [
        { "guid": "e94c2d6c", "name": "ET_OPMODE", "type": "DUT", "form": "ST_DECL",
          "path": "Device/Plc Logic/BuildingApplication/Enums/ET_OPMODE.st",
          "hash": "sha256:87c6", "bytes": 69 },
        { "guid": "83175985", "name": "Bibliotheksverwalter", "type": "LIBRARY_MANAGER",
          "form": "LIBRARIES",
          "path": "Device/Plc Logic/BuildingApplication/Bibliotheksverwalter.libraries.yaml",
          "hash": "sha256:b05d", "bytes": 5278 }
      ],
      "skipped": [
        { "name": "__VisualizationStyle", "path": "__VisualizationStyle",
          "type": "VISUALISATION_STYLE", "reason": "SERVICE_OBJECT" }
      ],
      "warnings": [
        { "code": "UNKNOWN_TYPE_GUID",
          "path": "Device\\Plc Logic\\BuildingApplication/license",
          "detail": "a56744ff-693f-4597-95f9-0e1c529fffc2" }
      ]
    }
    """;

    [Fact]
    public void A_manifest_carrying_warnings_keeps_every_object()
    {
        var manifest = ProjectManifest.Parse(RealManifest);

        Assert.Equal(3, manifest.Schema);
        Assert.Equal("d4e33864d816", manifest.BindingId);
        Assert.Equal(2, manifest.Objects.Count);
        Assert.Single(manifest.Skipped);
    }

    [Fact]
    public void A_warning_is_read_as_the_record_the_exporter_writes()
    {
        var warning = Assert.Single(ProjectManifest.Parse(RealManifest).Warnings);

        Assert.Equal("UNKNOWN_TYPE_GUID", warning.Code);
        Assert.Equal(@"Device\Plc Logic\BuildingApplication/license", warning.Path);
        Assert.Equal("a56744ff-693f-4597-95f9-0e1c529fffc2", warning.Detail);
    }

    [Fact]
    public void An_unreadable_field_costs_that_field_and_nothing_else()
    {
        // objectCount as a string is the drift this class is about, in miniature: a producer
        // changing one field's type must not be able to empty the panel.
        const string json = """
        {
          "schema": 3,
          "objectCount": "two",
          "objects": [
            { "guid": "a", "name": "Pump", "type": "POU", "form": "ST_DECL", "path": "App/Pump.st" }
          ],
          "warnings": "none at all"
        }
        """;

        var manifest = ProjectManifest.Parse(json, out var problems);

        Assert.Equal("Pump", Assert.Single(manifest.Objects).Name);
        Assert.Equal(3, manifest.Schema);
        Assert.Equal(0, manifest.ObjectCount);
        Assert.Empty(manifest.Warnings);

        // Both bad fields named, because a manifest silently half-read is how this started.
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("objectCount", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("warnings", StringComparison.Ordinal));
    }

    [Fact]
    public void One_unreadable_object_does_not_take_the_rest_with_it()
    {
        const string json = """
        {
          "schema": 3,
          "objects": [
            { "guid": "a", "name": "Pump", "type": "POU", "form": "ST_DECL", "path": "App/Pump.st" },
            { "guid": "b", "name": "Valve", "bytes": "a lot", "path": "App/Valve.st" },
            { "guid": "c", "name": "Fan", "type": "POU", "form": "ST_DECL", "path": "App/Fan.st" }
          ]
        }
        """;

        var manifest = ProjectManifest.Parse(json, out var problems);

        Assert.Equal(["Pump", "Fan"], manifest.Objects.Select(o => o.Name));
        Assert.Contains(problems, p => p.Contains("objects[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_that_is_not_json_at_all_is_empty_and_says_why()
    {
        var manifest = ProjectManifest.Parse("{ not json", out var problems);

        Assert.Empty(manifest.Objects);
        Assert.Single(problems);

        // An absent file is not a problem — it is a project that has never been exported —
        // so an empty string reports nothing to complain about.
        Assert.Empty(ProjectManifest.Parse(string.Empty, out var none).Objects);
        Assert.Empty(none);
    }

    [Fact]
    public void Keys_are_matched_however_they_are_cased()
    {
        // The old parser set PropertyNameCaseInsensitive and nothing measured whether any
        // producer relied on it. Kept rather than dropped: losing it would be a silent
        // behaviour change riding along with a bug fix.
        var manifest = ProjectManifest.Parse("""
        { "Schema": 3, "BindingId": "abc", "Objects": [
            { "Guid": "a", "Name": "Pump", "Type": "POU", "Form": "ST_DECL", "Path": "App/Pump.st" } ] }
        """);

        Assert.Equal(3, manifest.Schema);
        Assert.Equal("abc", manifest.BindingId);
        Assert.Equal("Pump", Assert.Single(manifest.Objects).Name);
    }

    [Fact]
    public void A_manifest_from_a_newer_versio_keeps_what_this_one_understands()
    {
        // The contract Parse has always claimed and could not honour: a field this build has
        // never heard of is ignored, not fatal.
        var manifest = ProjectManifest.Parse("""
        {
          "schema": 9,
          "objects": [ { "guid": "a", "name": "Pump", "type": "POU", "form": "ST_DECL",
                         "path": "App/Pump.st", "signature": { "by": "someone" } } ],
          "devices": [ { "name": "PLC_1" } ]
        }
        """, out var problems);

        Assert.Equal("Pump", Assert.Single(manifest.Objects).Name);
        Assert.Empty(problems);
    }
}
