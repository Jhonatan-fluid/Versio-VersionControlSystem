using System.Diagnostics;
using System.Text.Json;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The managed-library branch of <c>versio_libraries.py</c>, exercised by running the module.
/// </summary>
/// <remarks>
/// <para>
/// <b>G8, and it was open for a reason no amount of reading would have closed.</b> Every
/// reference in both fixtures is a placeholder — 13 in the project's own manager and 21 in the
/// application's, 34 in total — so the branch that shapes a fixed-version reference had never
/// executed in this repository's whole history. It was written from the API's description and
/// said so.
/// </para>
/// <para>
/// <b>What the probe measured</b> (P9, twice, on real CODESYS 3.5.22.20 — see
/// <c>docs/phase1/IMPORT-PLAN.md</c>). <c>add_library("Standard, 3.5.17.0 (System)")</c>
/// yields a <c>ScriptManagedLibraryReference</c> reading <c>is_managed: true</c>, whose
/// <c>managed_library</c> exists and whose every field — <c>version</c>, <c>company</c>,
/// <c>title</c>, <c>default_namespace</c> — throws <c>NullReferenceException</c>, because that
/// version is not installed on the machine. Add the version that <i>is</i> installed and the
/// same three fields answer <c>3.5.22.0</c> / <c>System</c> / <c>Standard</c>.
/// </para>
/// <para>
/// So the old branch had two faults, and the second is worse than the first. It read the fields
/// off <c>managed_library</c> through an exception-swallowing probe, so an unresolvable
/// reference was written with no version at all and no warning — indistinguishable in the file
/// from a placeholder that has none. And had it worked, it would have recorded <b>installation
/// state</b>: the same project would export differently for two engineers, which is the one
/// thing the file's own header refuses to do, and is G9's machine-dependent-warning defect
/// through a third door.
/// </para>
/// <para>
/// <b>Why a fake reference is honest here.</b> The plan's standing objection to hand-built
/// fixtures is that they assert the test's own assumptions. These fakes assert the <i>probe's</i>
/// measurements: each one is shaped like the object P9 printed, including which attributes are
/// absent and which raise. The real-CODESYS half of G8 is the probe; this is the half that runs
/// on every build and goes red when somebody edits the rule.
/// </para>
/// <para>
/// Skips cleanly without python on PATH, exactly as the CODESYS tests skip without CODESYS.
/// <c>versio_libraries.py</c> imports no <c>scriptengine</c> and is handed its objects and its
/// <c>safe</c> probe, which is what makes this possible at all — the same argument
/// <c>versio_types.CAN_VERIFY_FORMS</c> was moved for.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PythonLibraryRecordTests(ITestOutputHelper output)
{
    /// <summary>
    /// Names the parse must accept, and names it must refuse rather than guess at.
    /// </summary>
    /// <remarks>
    /// The refusals are the point. A managed reference's name is the only machine-independent
    /// description of which library it is, so the parse either reads it exactly or reports
    /// nothing: an absent version is a gap a reader can see, a wrong one is a diff somebody
    /// acts on. <c>"Weird, Name, 1.2.3.4 (Acme GmbH)"</c> is in the accepted half deliberately —
    /// splitting on the <i>last</i> separator is what makes a comma in a title survive.
    /// </remarks>
    public static TheoryData<string, string> Accepted =>
        new()
        {
            { "Standard, 3.5.22.0 (System)", "Standard|3.5.22.0|System" },
            { "Util, 3.5.17.0 (System)", "Util|3.5.17.0|System" },
            { "Weird, Name, 1.2.3.4 (Acme GmbH)", "Weird, Name|1.2.3.4|Acme GmbH" },
            { "X, 1 (C)", "X|1|C" },
        };

    public static TheoryData<string> Refused =>
        new()
        {
            "#Standard",                        // a placeholder, which never reaches this
            "",
            "Standard",
            "Standard, 3.5.22.0",               // no company
            "Standard, 3.5.22.0 ()",            // empty company
            ", 1.0.0.0 (System)",               // no title
            "Standard, x.y.z (System)",         // version is not digits and dots
            "Standard, ... (System)",           // dots but no digit

            // FOUND BY MUTATION, not by writing the list. The parse has two version rules —
            // "nothing but digits and dots" and "at least one digit" — and every case above
            // fails BOTH, so deleting the first left the whole corpus green. These two fail
            // only the first, which is the only way it is watched at all.
            "Standard, 3.5.22.0-beta (System)",
            "Standard, 1.0a (System)",
            "Standard, 3.5.22.0 (System) ",     // trailing space: the bracket is not last
            "Missing bracket, 1.0.0.0 System",
        };

    [SkippableTheory]
    [MemberData(nameof(Accepted))]
    public void A_managed_reference_name_yields_its_three_fields(string name, string expected)
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var got = Run("""
            import versio_libraries as L
            p = L.parse_managed_name(payload["name"])
            out = "" if p is None else "|".join([p["title"], p["version"], p["company"]])
            """,
            new { name });

        output.WriteLine($"{name} -> {got}");
        Assert.Equal(expected, got);
    }

    [SkippableTheory]
    [MemberData(nameof(Refused))]
    public void A_name_that_is_not_that_shape_yields_nothing(string name)
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var got = Run("""
            import versio_libraries as L
            out = "PARSED" if L.parse_managed_name(payload["name"]) is not None else "NONE"
            """,
            new { name });

        output.WriteLine($"{name} -> {got}");
        Assert.Equal("NONE", got);
    }

    [SkippableFact]
    public void A_managed_reference_records_the_version_the_project_asks_for()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        // THE REGRESSION GUARD FOR THE DEFECT ITSELF. This fake's `managed_library` raises the
        // way the real one does for a library that is not installed, and the record must carry
        // the version anyway — because it now comes from the name, which is project state. The
        // old branch produced {name, namespace, flags} here and warned about nothing.
        var record = Run("""
            import versio_libraries as L

            class Ref(object):
                name = u"Standard, 3.5.22.0 (System)"
                namespace = u"Standard"
                is_placeholder = False
                is_managed = True
                system_library = False
                optional = False
                qualified_only = True
                publish_symbols_in_container = False
                parameters = None

                @property
                def managed_library(self):
                    raise RuntimeError("Object reference not set to an instance of an object.")

            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default

            errors = []
            rec = L.extract_reference(Ref(), safe,
                                      lambda code, detail: errors.append(code))
            out = json.dumps({"rec": rec, "errors": errors}, sort_keys=True)
            """);

        output.WriteLine(record);
        using var doc = JsonDocument.Parse(record);
        var rec = doc.RootElement.GetProperty("rec");

        Assert.Equal("3.5.22.0", rec.GetProperty("version").GetString());
        Assert.Equal("System", rec.GetProperty("company").GetString());
        Assert.Equal("Standard", rec.GetProperty("title").GetString());
        Assert.True(rec.GetProperty("qualifiedOnly").GetBoolean());

        // A managed reference is not a placeholder and must not be given a placeholder's keys:
        // `defaultResolution` on a fixed-version reference would read as a wildcard request.
        Assert.False(rec.TryGetProperty("placeholder", out _));
        Assert.False(rec.TryGetProperty("defaultResolution", out _));

        // Nothing to report: the name parsed, so the record is complete.
        Assert.Empty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }

    [SkippableFact]
    public void A_managed_reference_whose_name_cannot_be_read_is_reported_not_guessed()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var record = Run("""
            import versio_libraries as L

            class Ref(object):
                name = u"SomethingNobodyHasSeen"
                namespace = u"Something"
                is_placeholder = False
                is_managed = True
                system_library = False
                optional = False
                qualified_only = False
                publish_symbols_in_container = False
                parameters = None

            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default

            errors = []
            rec = L.extract_reference(Ref(), safe,
                                      lambda code, detail: errors.append([code, detail]))
            out = json.dumps({"rec": rec, "errors": errors}, sort_keys=True)
            """);

        output.WriteLine(record);
        using var doc = JsonDocument.Parse(record);
        var rec = doc.RootElement.GetProperty("rec");

        // Still recorded — the name and the flags are real project state and worth diffing.
        Assert.Equal("SomethingNobodyHasSeen", rec.GetProperty("name").GetString());
        Assert.False(rec.TryGetProperty("version", out _));

        var errors = doc.RootElement.GetProperty("errors").EnumerateArray().ToList();
        var error = Assert.Single(errors);
        Assert.Equal("LIBRARY_MANAGED_NAME_UNPARSED", error[0].GetString());

        // The detail names the string that could not be read, because a code alone leaves the
        // reader grepping a 200-line YAML file for which of 34 references it meant.
        Assert.Equal("SomethingNobodyHasSeen", error[1].GetString());
    }

    [SkippableFact]
    public void A_placeholder_keeps_its_own_fields_and_gains_no_managed_ones()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        // The other half of the branch, and the half that could regress silently: this fake
        // raises AttributeError on `managed_library` exactly as ScriptPlaceholderReference does
        // (measured — P9), so a rule that reached for it on every reference would show up here.
        var record = Run("""
            import versio_libraries as L

            class Ref(object):
                name = u"#Standard"
                namespace = u"Standard"
                placeholder_name = u"Standard"
                default_resolution = u"Standard, * (System)"
                is_placeholder = True
                is_managed = False
                is_redirected = True
                system_library = False
                optional = False
                qualified_only = False
                publish_symbols_in_container = False
                parameters = None

                def get_redirection(self):
                    return u"Standard, 3.5.22.0 (System)"

            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default

            errors = []
            rec = L.extract_reference(Ref(), safe,
                                      lambda code, detail: errors.append(code))
            out = json.dumps({"rec": rec, "errors": errors}, sort_keys=True)
            """);

        output.WriteLine(record);
        using var doc = JsonDocument.Parse(record);
        var rec = doc.RootElement.GetProperty("rec");

        Assert.Equal("Standard", rec.GetProperty("placeholder").GetString());
        Assert.Equal("Standard, * (System)", rec.GetProperty("defaultResolution").GetString());
        Assert.Equal("Standard, 3.5.22.0 (System)", rec.GetProperty("redirection").GetString());

        // A redirection is a version, and it is NOT the `version` key: that one means "this
        // reference names a fixed library", and a placeholder does not.
        Assert.False(rec.TryGetProperty("version", out _));
        Assert.False(rec.TryGetProperty("title", out _));
        Assert.Empty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }

    [SkippableFact]
    public void A_reference_that_is_neither_kind_is_reported_rather_than_recorded_quietly()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        // Nothing in CODESYS 3.5.22.20 produces one — is_managed and is_placeholder are the two
        // discriminators the API offers and every reference answered true to exactly one. That
        // is why this is worth a code: if a future version adds a third kind, the record for it
        // carries no version, no placeholder and no explanation, and the file would read as
        // though the reference were incomplete rather than unrecognised.
        var record = Run("""
            import versio_libraries as L

            class Ref(object):
                name = u"SomeFutureKind"
                namespace = u"Future"
                is_placeholder = False
                is_managed = False
                system_library = False
                optional = False
                qualified_only = False
                publish_symbols_in_container = False
                parameters = None

            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default

            errors = []
            rec = L.extract_reference(Ref(), safe,
                                      lambda code, detail: errors.append(code))
            out = json.dumps({"rec": rec, "errors": errors}, sort_keys=True)
            """);

        output.WriteLine(record);
        using var doc = JsonDocument.Parse(record);
        Assert.Equal(
            "LIBRARY_REFERENCE_UNCLASSIFIED",
            Assert.Single(doc.RootElement.GetProperty("errors").EnumerateArray()).GetString());
    }

    private static string Run(string body, object? payload = null) =>
        CPythonHarness.RunJson(
            JsonSerializer.Serialize(payload ?? new { }), body, jsonOut: false);

    private static bool PythonIsAvailable() => CPythonHarness.PythonIsAvailable();
}
