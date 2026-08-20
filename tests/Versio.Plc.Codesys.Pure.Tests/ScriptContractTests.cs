using Versio.Core.Projects;
using Versio.Plc.Codesys.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Plc.Codesys.Tests;

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
/// the parity tests read <c>src\Versio.Plc.Codesys.Infrastructure\Scripts</c> because their subject is the source
/// of truth, but the subject here is what actually ships and runs. Editing a <c>.py</c> and
/// running <c>--no-build</c> tests the script embedded at the last build, and that trap has
/// already cost this project a mutation check that reported a disabled feature as working.
/// </para>
/// <para>
/// No CODESYS and no python: it reads text. It stays in the fast lane and out of
/// <see cref="CodesysCollection"/>, so it cannot run beside another CODESYS launch.
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
        var sources = new[] { "versio_export.py", "versio_libraries.py", "versio_tasks.py", "versio_devices.py" }
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
            "DEVICE_CONNECTORS_UNAVAILABLE",
            "DEVICE_CONNECTORS_PARTIAL",
            "DEVICE_SLOT_HAS_CHILDREN",
            "DEVICE_SLOT_INDEX_UNREADABLE",

            // G9's machine-independent guard. It fires BEFORE the write and does not stop
            // it, so on a machine where the path still fits the object is written and
            // recorded — the warning is about every OTHER machine, where this mirror cannot
            // be checked out. If the write does then fail, EXPORT_FAILED fires beside it and
            // that one is the failure. The first code this invariant forced somebody to
            // classify, which is what it was written for.
            "PATH_TOO_LONG",

            // G8's two, and they are the "incomplete content" kind above rather than the
            // losing kind: the reference is still written, with its name and its flags, and
            // the library manager's file is still recorded. What is missing is the version,
            // company and title of ONE reference — a gap inside a file that exists, which is
            // why it is a warning at all and why it names the reference in its detail.
            //
            // The alternative was to keep silent, and that is the defect this replaced: a
            // managed reference recorded without its version is indistinguishable in the YAML
            // from a placeholder that never had one.
            "LIBRARY_MANAGED_NAME_UNPARSED",
            "LIBRARY_REFERENCE_UNCLASSIFIED",
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
        Assert.Contains("DEVICE_CONNECTORS_PARTIAL", codes, StringComparer.Ordinal);
    }

    [Fact]
    public void A_failed_device_yaml_write_still_walks_the_subtree()
    {
        // Device used to be FORM_CONTAINER: no write, children walked anyway. The
        // FORM_DEVICES except used to return before the walk, so a disk error on
        // Device.devices.yaml dropped Plc Logic from the swapped mirror. The except
        // must warn and fall through to `for child in children`.
        var source = WithoutComments(Exporter());
        var devices = source.IndexOf("if form == FORM_DEVICES:", StringComparison.Ordinal);
        Assert.True(devices >= 0, "FORM_DEVICES branch missing from versio_export.py");

        var exceptAt = source.IndexOf("except Exception", devices, StringComparison.Ordinal);
        var walkAt = source.IndexOf("for child in children:", devices, StringComparison.Ordinal);
        Assert.True(exceptAt >= 0 && walkAt > exceptAt,
            "FORM_DEVICES must warn on EXPORT_FAILED and then walk children");

        var exceptBlock = source[exceptAt..walkAt];
        Assert.DoesNotContain("return", exceptBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void The_machine_dependent_path_warning_never_reaches_the_manifest()
    {
        // THE ONE RULE THIS PAIR OF GUARDS TURNS ON, and nothing else can enforce it.
        //
        // PATH_TOO_LONG_HERE answers "is this repository checked out too deep", which is a
        // different answer on every machine. Exporter.warn appends to self.warnings and that
        // list is written into _manifest.json — so routing this through warn() would make the
        // manifest differ between two developers exporting the same project, and the mirror
        // would churn on every export by whoever is checked out deeper. That is exactly the
        // churn Phase 5 closed, arriving through a door Phase 5 could not have known about.
        //
        // So it goes to emit() and no further, the way PROJECT_CLOSE_FAILED does. Asserted
        // rather than commented, because the difference between the two spellings is one
        // word at the call site and the consequence is a mirror that never settles.
        var source = WithoutComments(Exporter());

        Assert.False(source.Contains("self.warn(\"PATH_TOO_LONG_HERE\"", StringComparison.Ordinal),
            "PATH_TOO_LONG_HERE is going through warn(), so it will be written into "
            + "_manifest.json. Its answer depends on the repository root, so the manifest "
            + "would differ between developers and the mirror would churn on every export — "
            + "Phase 5's defect, through a new door. Use emit() directly.");

        // Non-vacuous: the code really is raised, and really is raised the other way.
        Assert.Contains("\"code\": \"PATH_TOO_LONG_HERE\"", source, StringComparison.Ordinal);

        // And its machine-INDEPENDENT sibling really does go through warn(), or the manifest
        // records nothing about paths at all and this test is guarding an empty room.
        Assert.Contains("self.warn(\"PATH_TOO_LONG\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_file_the_export_writes_has_its_path_length_checked()
    {
        // THE OMISSION THIS EXISTS FOR, found by re-reading the guard rather than by any
        // test of it. G9's check measured the object's PRIMARY path only, and a two-file
        // form writes a second one: a GVL_BOTH's .gvl.xml, which is a character LONGER than
        // the .gvl.st beside it. So the one file a GVL could overflow on first was the file
        // nothing looked at.
        //
        // It is the G7 and G12 shape for the third time — a rule written for objects,
        // applied to something that writes two files. TASK_BOTH escaped only by luck, its
        // .tasks.yaml being longer than its .xml, which is exactly the kind of luck that
        // stops holding when a suffix changes.
        var source = WithoutComments(Exporter());

        var secondFiles = System.Text.RegularExpressions.Regex
            .Matches(source, @"xml_path = os\.path\.join\(").Count;
        // `self.` prefixed, so the `def check_path_length(` line is not counted as a call.
        // WITHOUT IT THIS TEST PASSED ITS OWN MUTATION: definition plus two calls is three,
        // which cleared a `>= 3` bar that the three real calls were supposed to clear. It
        // looked right, and it was watching nothing at exactly the boundary it existed for.
        var checks = System.Text.RegularExpressions.Regex
            .Matches(source, @"self\.check_path_length\(").Count;

        output.WriteLine($"second-file writes: {secondFiles}, path checks: {checks}");

        // Non-vacuous: there really are two-file forms to miss. THREE of them since
        // 2026-08-18, when PROPERTY_BOTH joined GVL_BOTH and TASK_BOTH — a property writes
        // its recursive .xml and its readable .st, and the .xml is built the same way the
        // other two build theirs, which is why this count moves rather than the pattern.
        Assert.Equal(3, secondFiles);

        // One for the primary path, one for each second file. Counting rather than matching
        // positions because the two branches build their paths differently and a positional
        // rule would break on a refactor that is not a regression.
        Assert.True(checks >= secondFiles + 1,
            $"versio_export.py writes {secondFiles} second file(s) and calls "
            + $"check_path_length {checks} time(s). Every file the export writes needs its "
            + "own check — the guard measures a PATH, and a two-file object has two of them.");
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
