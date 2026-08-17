using Versio.Core.Projects;
using Versio.Infrastructure.Plc.Codesys;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The stable machine strings C# reads out of a manifest, checked against the script that
/// writes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class had to exist.</b> Every test of <see cref="ExportCoverage"/> builds its
/// fixture from <see cref="ExportCoverage"/>'s own constants, so both sides of the comparison
/// move together: misspelling <see cref="ExportCoverage.FailureCode"/> leaves the entire pure
/// suite green — measured, by doing it, at 1,492 passing with the constant reading
/// <c>EXPORT_FAILEDX</c>. A code that no script emits is a rule that silently never fires,
/// which is indistinguishable from an export that never fails.
/// </para>
/// <para>
/// This is the same C#/python drift <c>CODESYS.md</c> §9 counts four instances of, one level
/// down: not two implementations of an algorithm disagreeing, but one side reading a literal
/// the other side stopped writing. <c>TypeTableParityTests</c> covers the form names and
/// <c>PythonFileNameParityTests</c> covers the algorithms; the skip reasons and warning codes
/// had nothing.
/// </para>
/// <para>
/// <b>It reads the EMBEDDED copy, deliberately</b>, through <see cref="ScriptExtractor"/> —
/// the parity tests read <c>src\Versio.Scripts.Codesys</c> because their subject is the source
/// of truth, but the subject here is what actually ships and runs. Editing a <c>.py</c> and
/// running <c>--no-build</c> tests the script embedded at the last build, and that trap has
/// already cost this project a mutation check that reported a disabled feature as working.
/// </para>
/// <para>
/// No CODESYS and no python: it reads text. It stays in the fast lane and out of
/// <see cref="CodesysCollection"/>, which <see cref="FastLaneTests"/> asserts from metadata.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ScriptContractTests(ITestOutputHelper output)
{
    private static string Exporter() =>
        File.ReadAllText(new ScriptExtractor().PathFor("versio_export.py"));

    /// <summary>The script with its full-line comments removed.</summary>
    /// <remarks>
    /// <para>
    /// For the assertions about the SHAPE of the code, which a comment must not be able to
    /// satisfy or to break. The second of those is not hypothetical: the path-spelling test
    /// below went red on its first run against a paragraph that quotes the inline expression
    /// it forbids — the comment explaining the fix looked exactly like the defect.
    /// </para>
    /// <para>
    /// Full-line comments only. A trailing <c>#</c> cannot be told from one inside a string
    /// literal without parsing python, and this file has both — object names beginning
    /// <c>#</c> are how CODESYS spells a placeholder. Prose is what needs excluding and prose
    /// lives on its own line, so the cheap rule is the correct one here rather than merely
    /// the easy one.
    /// </para>
    /// </remarks>
    private static string WithoutComments(string source) =>
        string.Join('\n', source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));

    [Fact]
    public void The_code_for_an_object_that_could_not_be_written_is_the_one_the_exporter_emits()
    {
        // Comments stripped, so a test satisfied by a paragraph cannot go on passing through
        // the one change that matters — somebody renaming the code and leaving the prose
        // above it saying the old name.
        var source = WithoutComments(Exporter());

        var call = $"self.warn(\"{ExportCoverage.FailureCode}\"";

        output.WriteLine($"looking for: {call}");

        Assert.True(source.Contains(call, StringComparison.Ordinal),
            $"ExportCoverage.FailureCode is \"{ExportCoverage.FailureCode}\", and no "
            + "versio_export.py warning is raised with it. ExportCoverage.Of reads this code "
            + "to report an object the export could not write; a code no script emits means "
            + "a failed object is silently absent from the mirror, the manifest and the "
            + "panel — which is the state this constant was added to end.");

        // NON-VACUOUS. Contains() against a 40 KB file passes for a great many strings, so
        // the shape of the search is asserted to fail on a code no script has.
        Assert.False(source.Contains("self.warn(\"EXPORT_FAILED_NOT_A_REAL_CODE\"",
                                     StringComparison.Ordinal));
    }

    [Fact]
    public void The_reason_for_a_type_nobody_has_written_the_code_for_is_the_one_the_scripts_use()
    {
        // The constant that was already here, pinned by the same argument. It travels a
        // different road — versio_types.SKIP_GAP, written into each skip record rather than
        // emitted as a warning — so it is asserted against its definition rather than
        // against a call.
        var types = WithoutComments(
            File.ReadAllText(new ScriptExtractor().PathFor("versio_types.py")));
        var definition = $"SKIP_GAP = \"{ExportCoverage.GapReason}\"";

        output.WriteLine($"looking for: {definition}");

        Assert.True(types.Contains(definition, StringComparison.Ordinal),
            $"ExportCoverage.GapReason is \"{ExportCoverage.GapReason}\" and versio_types.py "
            + "defines SKIP_GAP as something else, so every real gap would be counted as a "
            + "deliberate skip and the coverage verdict would read complete.");

        Assert.False(types.Contains("SKIP_GAP = \"NOT_A_REAL_REASON\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_exporter_warning_is_classified_as_losing_an_object_or_not()
    {
        // THE INVARIANT, and it is here rather than a comment because this class has now
        // twice been the thing that recurred. ExportCoverage.IsFailure names the codes that
        // mean "this object is not in the mirror at all"; every other code the exporter can
        // raise has to be a deliberate entry below. A code in neither list fails this test,
        // so the next warning somebody adds is a decision rather than a door.
        //
        // EXPORT_FAILED was found by tracing it; UNNAMED_OBJECT was found by re-reading the
        // same function afterwards and is one step earlier — it returns before `total` is
        // even incremented. One was closed and the other would have been missed, which is
        // precisely how G7 became G12.
        // TWO SPELLINGS, because the first version of this test only had one and therefore
        // was not the "every" its name claims. `self.warn("CODE"` catches the exporter's own
        // literals; the library and task readers raise theirs through an `on_error` callback
        // the exporter hands them, so their codes reach `manifest.warnings` without ever
        // appearing next to a `warn` in versio_export.py at all. A scan that missed them was
        // a door of exactly the kind this test exists to close.
        //
        // Not scanned, and correctly: PROJECT_CLOSE_FAILED goes straight to `emit` rather
        // than through `warn`, so it never enters `self.warnings` and never reaches the
        // manifest. It is a protocol event, not a coverage fact.
        var extractor = new ScriptExtractor();
        var sources = new[] { "versio_export.py", "versio_libraries.py", "versio_tasks.py" }
            .Select(name => WithoutComments(File.ReadAllText(extractor.PathFor(name))));

        var codes = sources
            .SelectMany(source => System.Text.RegularExpressions.Regex
                .Matches(source, "(?:self\\.warn|on_error)\\(\"([A-Z_]+)\"")
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        // Warned about and still written. Each of these leaves the object in `records` or in
        // `skipped`, so coverage already accounts for it and raising the alarm again would
        // train people past the alarm that matters.
        string[] recordedAnyway =
        [
            // Warns, then falls through to decide_form — the object is exported natively and
            // recorded. It is the case that proves not every warning is a loss.
            "UNKNOWN_TYPE_GUID",

            // A DIFFERENT KIND OF GAP, and one nothing measures yet: the file is written and
            // recorded, with content that may be short of what the project holds. The object
            // is in the mirror, so it is not a failure by this rule — but "exported with
            // incomplete content" is its own unanswered question, and these four are where
            // it would be asked. Listed here so that stays visible rather than implied.
            "LIBRARY_REFERENCES_UNAVAILABLE",
            "LIBRARY_REFERENCES_PARTIAL",
            "TASK_CHILDREN_UNAVAILABLE",
            "TASK_LIST_PARTIAL",
        ];

        foreach (var code in codes)
        {
            output.WriteLine(
                $"{code,-20} {(ExportCoverage.IsFailure(code) ? "loses the object" : "recorded anyway")}");
        }

        var unclassified = codes
            .Where(c => !ExportCoverage.IsFailure(c) && !recordedAnyway.Contains(c, StringComparer.Ordinal))
            .ToList();

        Assert.True(unclassified.Count == 0,
            "versio_export.py raises warning code(s) that ExportCoverage neither treats as "
            + "losing an object nor this test lists as harmless: "
            + string.Join(", ", unclassified)
            + ". Decide which it is. If the warn is followed by a `return` out of the walk, "
            + "the object reaches neither `records` nor `skipped` and the code belongs in "
            + "ExportCoverage.IsFailure; if the object is still written or still skipped, add "
            + "it to recordedAnyway here with the reason.");

        // NON-VACUOUS twice over: the scan really found codes, and it found both of the ones
        // that lose an object. An empty `codes` would satisfy the assertion above in silence.
        Assert.Contains(ExportCoverage.FailureCode, codes, StringComparer.Ordinal);
        Assert.Contains(ExportCoverage.UnnamedCode, codes, StringComparer.Ordinal);
        Assert.Contains("UNKNOWN_TYPE_GUID", codes, StringComparer.Ordinal);

        // And that the second spelling really reached the other two modules — without this
        // the on_error half could stop matching and the scan would quietly shrink back to
        // what it was when it was not "every".
        Assert.Contains("TASK_LIST_PARTIAL", codes, StringComparer.Ordinal);
        Assert.Contains("LIBRARY_REFERENCES_PARTIAL", codes, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_warning_the_exporter_raises_travels_the_one_path_spelling()
    {
        // The rule versio_text.object_path exists to hold. Two of the exporter's five
        // object-path sites built the string inline and skipped the normalisation, so the
        // same object was `App/Thing` in _manifest.json's skips and `\App/Thing` in its
        // warnings — different separators, and a leading slash at the project root.
        //
        // It stopped being cosmetic when ExportCoverage began printing the EXPORT_FAILED
        // path in the panel: a name with a stray backslash is a name nobody can search the
        // mirror for.
        var source = WithoutComments(Exporter());

        Assert.False(source.Contains("rel_dir + \"/\" + name", StringComparison.Ordinal),
            "an object path is being built inline in versio_export.py again. Use "
            + "versio_text.object_path — it is the one spelling shared by the manifest, "
            + "_coverage.md, the log and the panel.");

        // A RAW rel_dir is the same defect wearing different clothes, and it is the one the
        // first version of this test did not catch: UNNAMED_OBJECT passed os.path.join's
        // output straight into warn() while the other four sites were being converted, so
        // the code that ExportCoverage.IsFailure had just been taught to read was the one
        // still spelling its path with backslashes.
        Assert.False(source.Contains("self.warn(\"UNNAMED_OBJECT\", rel_dir)", StringComparison.Ordinal),
            "UNNAMED_OBJECT is passing a raw rel_dir. It is an ExportCoverage.IsFailure code, "
            + "so that directory is printed in the panel as the object's only description — "
            + "use versio_text.object_path(rel_dir, None).");

        // Non-vacuous the other way: the helper really is what the file calls now, in both
        // its shapes — with a name, and without one.
        Assert.Contains("object_path(rel_dir, name)", source, StringComparison.Ordinal);
        Assert.Contains("object_path(rel_dir, None)", source, StringComparison.Ordinal);
    }
}
