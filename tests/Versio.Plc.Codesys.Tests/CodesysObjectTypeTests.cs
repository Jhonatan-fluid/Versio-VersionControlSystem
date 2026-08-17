using System.Text.RegularExpressions;
using Versio.Core.Projects;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

public class ObjectTypeTests
{
    [Theory]
    [InlineData("6f9dac99-8de1-4efc-8465-68ac443b7d08", CodesysObjectType.Pou)]
    [InlineData("2db5746d-d284-4425-9f7f-2663a34b0ebc", CodesysObjectType.Dut)]
    [InlineData("ffbfa93a-b94d-45fc-a329-229860183b1d", CodesysObjectType.Gvl)]
    [InlineData("f18bec89-9fef-401d-9953-2f11739a6808", CodesysObjectType.Visualisation)]
    [InlineData("738bea1e-99bb-4f04-90bb-a7a567e74e3a", CodesysObjectType.Folder)]
    public void Known_guids_map_to_their_type(string guid, CodesysObjectType expected)
    {
        Assert.Equal(expected, CodesysObjectTypes.FromGuid(guid));
    }

    [Theory]
    [InlineData("40b404f9-e5dc-42c6-907f-c89f4a517386", CodesysObjectType.PlcLogic)]
    [InlineData("639b491f-5557-464c-af91-1471bac9f549", CodesysObjectType.Application)]
    public void Types_discovered_by_probing_a_real_SP22_project_are_mapped(
        string guid, CodesysObjectType expected)
    {
        // Not present in codescribe's table. Found by running versio_probe.py against
        // RefrigeratorControl.project on CODESYS 3.5.22.20 - they showed up as UNKNOWN.
        // Locating Application by type GUID is more robust than by name, which is what
        // codescribe does and which breaks if a project renames the node.
        Assert.Equal(expected, CodesysObjectTypes.FromGuid(guid));
    }

    [Fact]
    public void Guid_lookup_is_case_insensitive()
    {
        Assert.Equal(CodesysObjectType.Pou,
            CodesysObjectTypes.FromGuid("6F9DAC99-8DE1-4EFC-8465-68AC443B7D08"));
    }

    [Fact]
    public void Whitespace_is_tolerated()
    {
        Assert.Equal(CodesysObjectType.Pou,
            CodesysObjectTypes.FromGuid("  6f9dac99-8de1-4efc-8465-68ac443b7d08  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Unmapped_input_is_Unknown_and_never_throws(string? guid)
    {
        // An unmapped GUID must degrade to a loud warning plus native export, never a crash
        // and never a silent drop.
        Assert.Equal(CodesysObjectType.Unknown, CodesysObjectTypes.FromGuid(guid));
    }

    [Fact]
    public void The_mapping_is_bijective()
    {
        // Two types sharing a GUID would make the export ambiguous.
        Assert.Equal(CodesysObjectTypes.All.Count,
                     CodesysObjectTypes.All.Values.Distinct().Count());
    }

    [Fact]
    public void Round_trip_guid_to_type_and_back()
    {
        foreach (var (guid, type) in CodesysObjectTypes.All)
        {
            Assert.Equal(guid, CodesysObjectTypes.ToGuid(type));
        }
    }

    [Fact]
    public void Every_mapped_guid_is_well_formed()
    {
        foreach (var guid in CodesysObjectTypes.All.Keys)
        {
            Assert.True(Guid.TryParse(guid, out _), $"malformed type GUID: {guid}");
        }
    }
}

/// <summary>
/// The C# table and the python table are two copies of the same knowledge. If they drift,
/// the exporter and the app disagree about what an object is - and that is a silent,
/// data-shaped bug. Compared directly rather than trusted.
/// </summary>
public class TypeTableParityTests
{
    private static readonly Regex Entry =
        new("""^\s*"(?<guid>[0-9a-fA-F-]{36})"\s*:\s*"(?<name>[A-Z_]+)",""",
            RegexOptions.Multiline);

    private static string PythonTablePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "versio_types.py");
        return File.Exists(local) ? local : throw new FileNotFoundException(local);
    }

    [Fact]
    public void Python_and_csharp_type_tables_contain_the_same_guids()
    {
        var text = File.ReadAllText(PythonTablePath());
        var pythonGuids = Entry.Matches(text)
            .Select(m => m.Groups["guid"].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var csharpGuids = CodesysObjectTypes.All.Keys
            .Select(g => g.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var onlyPython = pythonGuids.Except(csharpGuids).ToList();
        var onlyCsharp = csharpGuids.Except(pythonGuids).ToList();

        Assert.True(onlyPython.Count == 0, $"only in versio_types.py: {string.Join(", ", onlyPython)}");
        Assert.True(onlyCsharp.Count == 0, $"only in ObjectType.cs: {string.Join(", ", onlyCsharp)}");
    }

    [Fact]
    public void Both_tables_carry_the_two_types_discovered_by_probing()
    {
        var text = File.ReadAllText(PythonTablePath());

        Assert.Contains("40b404f9-e5dc-42c6-907f-c89f4a517386", text);
        Assert.Contains("639b491f-5557-464c-af91-1471bac9f549", text);
    }

    [Fact]
    public void Python_and_csharp_agree_on_which_forms_are_never_applied_back()
    {
        // The seam that did not exist when it was needed.
        //
        // One-wayness is decided twice: versio_types.EXPORT_ONLY_FORMS drives the
        // "Exported for review, not applied back" section of _coverage.md, and
        // ExportCoverage.IsExportOnly drives the panel's lock glyph. Neither can see the
        // other, and disagreeing is worse than either being wrong alone — the file is
        // announced in the markdown nobody has open and looks freely editable in the app
        // somebody is actually staging from, which reads as a considered "this one is fine".
        //
        // TASK_NATIVE is why this is here. When Phase 4 made .tasks.yaml the importable half,
        // the .xml beside it stopped being applied and NOTHING went red, on either side.
        var text = File.ReadAllText(PythonTablePath());
        var block = Between(text, "EXPORT_ONLY_FORMS = (", ")");

        // The tuple holds constant NAMES, so each one is resolved to the string it is
        // assigned. Comparing names would pass while the two sides used different spellings
        // of the same idea, which is the failure this is here to catch.
        var pythonForms = Regex
            .Matches(block, @"\bFORM_[A-Z_]+\b")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                var assigned = Regex.Match(
                    text, $"^{name} = \"(?<value>[A-Z_]+)\"", RegexOptions.Multiline);
                Assert.True(assigned.Success,
                    $"{name} is in EXPORT_ONLY_FORMS with no literal assignment above it");
                return assigned.Groups["value"].Value;
            })
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(pythonForms);

        // Both directions. A form only python knows leaves the glyph off a file that is
        // silently discarded; a form only C# knows locks a row the importer applies fine.
        foreach (var form in pythonForms)
        {
            Assert.True(ExportCoverage.IsExportOnly(form),
                $"versio_types.py calls {form} export-only and ExportCoverage does not, so " +
                "the file is named in _coverage.md and still looks editable in the panel");
        }

        foreach (var form in new[]
                 {
                     ExportCoverage.LibrariesForm,
                     ExportCoverage.TaskNativeForm,
                     ExportCoverage.GvlNativeForm,
                 })
        {
            Assert.True(pythonForms.Contains(form),
                $"ExportCoverage calls {form} export-only and versio_types.py does not, so " +
                "the panel locks a row that _coverage.md never mentions");
        }

        // The form the task XML used to carry. It is a real, importable form — a property's
        // recursive export — and the whole defect was that the two were indistinguishable.
        Assert.False(ExportCoverage.IsExportOnly("NATIVE_R"));

        // ── THE THIRD HOME, which this test did not know about until it cost a run ──
        //
        // tools/codesys/New-ImportPlan.ps1 builds the plan for everything in tools/ — the round-trip
        // harness and the P7 stability measurement — because those drive the scripts raw,
        // without LocalCodesysRunner, so they need a plan from somewhere. It has its own copy
        // of the export-only list, and it REFUSES a form it does not know rather than guessing.
        //
        // That refusal is right and the omission was not: adding GVL_NATIVE for G12 left this
        // file behind, and every tool in that directory died on
        // `form 'GVL_NATIVE' is not one this helper knows` the next time one was run. Nothing
        // caught it, because nothing builds or tests PowerShell — so it is compared here, by
        // reading the source, exactly as the python list above is.
        // tools/codesys/ since 2026-08-17, when the seven CODESYS probes moved out of tools/
        // to sit beside the vendor's own projects. This path is BUILT rather than written as one
        // string, so the sweep that repointed every literal reference to the moved files walked
        // straight past it — and it broke in the 32-minute suite rather than in the eleven-second
        // one, which is the worst place to learn it.
        var helper = File.ReadAllText(Path.Combine(
            TestPaths.Root, "tools", "codesys", "New-ImportPlan.ps1"));

        var branch = Regex.Match(helper, @"'\^\((?<forms>[A-Z_|]+)\)\$'\s*\{\s*\$action = 'skip'");
        Assert.True(branch.Success,
            "New-ImportPlan.ps1 has no export-only branch this test can read — if the switch " +
            "was reshaped, reshape this pattern with it rather than deleting the check");

        var helperForms = branch.Groups["forms"].Value
            .Split('|')
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            pythonForms.OrderBy(f => f, StringComparer.Ordinal),
            helperForms.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_export_form_has_a_python_suffix()
    {
        // Regression, and the reason this test exists at all.
        //
        // FORM_GVL_BOTH was missing from the python SUFFIX table, so file_name_for() fell
        // through to the ".xml" default. The GVL's ST write then landed on the same path as
        // its native export and destroyed it - producing a file named .gvl.xml that
        // actually contained structured text. The C# PathMapper had it right; only the
        // python had drifted, and comparing GUID tables alone did not catch it.
        var text = File.ReadAllText(PythonTablePath());

        string[] requiredForms =
            ["FORM_ST_FULL", "FORM_ST_DECL", "FORM_ST_IMPL", "FORM_GVL_BOTH",
             "FORM_NATIVE", "FORM_NATIVE_R", "FORM_NATIVE_WITH_CHILDREN"];

        var suffixBlock = Between(text, "SUFFIX = {", "}");

        foreach (var form in requiredForms)
        {
            Assert.True(suffixBlock.Contains(form, StringComparison.Ordinal),
                $"{form} has no entry in the python SUFFIX table, so it will silently " +
                "fall through to the .xml default and can collide with another write");
        }
    }

    [Fact]
    public void Python_and_csharp_agree_on_the_type_infixes()
    {
        // The infix is what keeps a "Main" visualisation from colliding with a "Main" POU,
        // and a GVL's two files apart. Divergence here means one side overwrites a file the
        // other expects to exist.
        var infixBlock = Between(File.ReadAllText(PythonTablePath()), "INFIX = {", "}");

        Assert.Contains("\".vis\"", infixBlock);
        Assert.Contains("\".gvl\"", infixBlock);
        Assert.Contains("\".action\"", infixBlock);
        Assert.Contains("\".transition\"", infixBlock);
    }

    private static string Between(string text, string start, string end)
    {
        var i = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"could not find '{start}' in versio_types.py");
        var j = text.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j > i, $"could not find '{end}' after '{start}' in versio_types.py");
        return text[i..j];
    }
}
