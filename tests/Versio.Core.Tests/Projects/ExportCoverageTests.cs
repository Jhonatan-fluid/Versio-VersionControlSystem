using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// What the export left out, and whether that matters.
/// </summary>
/// <remarks>
/// The distinction under test is the whole feature: "not exported" covers a service object
/// whose content the template carries, and an object type nobody has written the code for
/// yet. One of those loses nothing and the other quietly breaks the tool's central claim,
/// and until this existed they looked identical from the outside.
/// </remarks>
public sealed class ExportCoverageTests
{
    private static SkippedObject Skip(string type, string reason, string? path = null) =>
        new()
        {
            Name = type,
            Type = type,
            Path = path ?? $"Device/{type}",
            Reason = reason,
        };

    private static ExportWarning Warning(string code, string path) =>
        new() { Code = code, Path = path, Detail = "detail the script sent" };

    /// <summary>An <c>EXPORT_FAILED</c> warning, which is all a failed object leaves behind.</summary>
    private static ExportWarning Failure(string path) =>
        Warning(ExportCoverage.FailureCode, path);

    private static ProjectManifest Manifest(params SkippedObject[] skipped) =>
        Manifest([], skipped);

    private static ProjectManifest Manifest(
        ExportWarning[] warnings, params SkippedObject[] skipped) =>
        new()
        {
            Schema = ExportCoverage.ReportingSchema,
            Project = "Machine.project",
            Objects = [new ProjectObject { Path = "App/Main.st", Guid = "g1" }],
            Skipped = skipped,
            Warnings = warnings,
        };

    [Fact]
    public void An_export_that_left_nothing_out_is_complete()
    {
        var report = ExportCoverage.Of(Manifest());

        Assert.Equal(CoverageVerdict.Complete, report.Verdict);
        Assert.False(report.HasGaps);

        // No badge on a clean export. A row that congratulated itself on every project every
        // refresh would be read past within a day, including the once it mattered.
        Assert.Equal(string.Empty, report.Label);
    }

    [Fact]
    public void Service_objects_left_out_deliberately_are_still_complete_coverage()
    {
        var report = ExportCoverage.Of(Manifest(
            Skip("VISUALISATION_MANAGER", "SERVICE_OBJECT"),
            Skip("TASK", "COVERED_ELSEWHERE"),
            Skip("PROJECT_SETTINGS", "TEMPLATE_ONLY")));

        Assert.Equal(CoverageVerdict.CompleteByDesign, report.Verdict);
        Assert.False(report.HasGaps);
        Assert.Equal(3, report.DeliberateSkips);
        Assert.Empty(report.Gaps);
        Assert.Equal(string.Empty, report.Label);
    }

    [Fact]
    public void An_unimplemented_type_is_a_gap_and_says_what_cannot_be_relied_on()
    {
        // The case that prompted the feature, kept as the example because it is the one
        // everybody here recognises — LIBRARY_MANAGER was skipped while a comment promised a
        // config file nothing wrote, so a library version bump changed nothing in the mirror.
        //
        // It is HISTORY now: library references are exported from manifest schema 3, and the
        // real fixture reports no gaps. The manifest below is hand-built precisely so the
        // reporting rules keep being tested after the gap that prompted them was closed —
        // they exist for the next one, not for that one.
        var report = ExportCoverage.Of(Manifest(
            Skip("LIBRARY_MANAGER", ExportCoverage.GapReason),
            Skip("VISUALISATION_MANAGER", "SERVICE_OBJECT")));

        Assert.Equal(CoverageVerdict.HasGaps, report.Verdict);
        Assert.True(report.HasGaps);
        Assert.Equal("LIBRARY_MANAGER", Assert.Single(report.Gaps).Type);
        Assert.Equal(1, report.DeliberateSkips);
        Assert.Equal("1 object not exported", report.Label);

        var sentence = report.Describe();

        // Names the thing, and what follows from it. "1 object was skipped" is a fact
        // nobody can act on.
        Assert.Contains("LIBRARY_MANAGER", sentence, StringComparison.Ordinal);
        Assert.Contains("will not appear in the mirror", sentence, StringComparison.Ordinal);
        Assert.Contains("_coverage.md", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_written_before_coverage_was_recorded_says_it_does_not_know()
    {
        // The vacuous answer this type exists to avoid: an old manifest lists nothing skipped
        // because nobody was counting, and reading that as full coverage would turn every
        // stale export into a clean bill of health.
        var report = ExportCoverage.Of(new ProjectManifest { Schema = 1 });

        Assert.Equal(CoverageVerdict.Unknown, report.Verdict);
        Assert.False(report.HasGaps);
        Assert.Equal("coverage unknown", report.Label);
        Assert.Contains("cannot be stated", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_manifest_is_unknown_rather_than_complete()
    {
        // A project that has never been exported has no coverage to report, and claiming it
        // covered everything would be the same lie one step earlier.
        var report = ExportCoverage.Of(null);

        Assert.Equal(CoverageVerdict.Unknown, report.Verdict);
        Assert.False(report.HasGaps);
    }

    [Fact]
    public void A_file_that_is_exported_but_never_applied_back_is_counted_and_said_out_loud()
    {
        // The third kind of content in an export tree, after objects and Versio's own
        // bookkeeping: real project state, written so a change reaches the diff, with no
        // import path that can be trusted. Library references are the first of them.
        //
        // It is NOT a gap — the content is in the mirror and a change to it does reach a
        // commit — but a file that looks editable and is silently ignored is its own kind of
        // hole, so the sentence has to carry it.
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject { Path = "App/Main.st", Form = "ST_FULL", Guid = "g1" },
                new ProjectObject
                {
                    Path = "App/Library Manager.libraries.yaml",
                    Form = ExportCoverage.LibrariesForm,
                    Type = "LIBRARY_MANAGER",
                    Guid = "g2",
                },
            ],
        };

        var report = ExportCoverage.Of(manifest);

        Assert.Equal(CoverageVerdict.Complete, report.Verdict);
        Assert.False(report.HasGaps);
        Assert.Equal(1, report.ExportOnlyFiles);

        var sentence = report.Describe();
        Assert.Contains("Everything in this project reaches the mirror", sentence,
                        StringComparison.Ordinal);
        Assert.Contains("not applied back", sentence, StringComparison.Ordinal);

        // It points at _coverage.md rather than naming a remedy. The sentence used to say
        // "library references … change those in the IDE", which was true only while libraries
        // were the sole one-way form: a task configuration's XML is fixed by editing the
        // .tasks.yaml beside it, and the remedy is per file. Stating it here as well would be
        // a second copy of a rule that already has a home, and the copy is what goes stale.
        Assert.Contains("_coverage.md", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("change those in the IDE", sentence, StringComparison.Ordinal);

        // Still no badge: this is a property of the format, not a problem with the export.
        // A row that said something on every project with a library manager — which is every
        // project — would be read past within a day.
        Assert.Equal(string.Empty, report.Label);
    }

    [Fact]
    public void A_device_comment_catalog_is_imported_and_cannot_delete_the_device()
    {
        // DEVICES stays export-only (CODESYS I/O). DEVICE_COMMENT is the measured
        // Comment SetAttribute path: the lock glyph must not say export-only, and a
        // missing file must not vote to unplug the station.
        Assert.True(ExportCoverage.IsExportOnly(ExportCoverage.DevicesForm));
        Assert.False(ExportCoverage.IsExportOnly(ExportCoverage.DeviceCommentForm));
        Assert.True(ExportCoverage.IsDeviceStructure(ExportCoverage.DevicesForm));
        Assert.True(ExportCoverage.IsDeviceStructure(ExportCoverage.DeviceCommentForm));
    }

    [Fact]
    public void An_export_with_no_one_way_files_says_nothing_about_them()
    {
        // The sentence appears only when there is something to say. Volunteering "0 files are
        // exported one way" on every clean project is how the useful half stops being read.
        var report = ExportCoverage.Of(Manifest());

        Assert.Equal(0, report.ExportOnlyFiles);
        Assert.DoesNotContain("not applied back", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_gaps_of_one_type_are_named_once()
    {
        var report = ExportCoverage.Of(Manifest(
            Skip("LIBRARY_MANAGER", ExportCoverage.GapReason, "Device/Libs"),
            Skip("LIBRARY_MANAGER", ExportCoverage.GapReason, "Device2/Libs")));

        Assert.Equal("2 objects not exported", report.Label);

        var sentence = report.Describe();
        Assert.Equal(1, Occurrences(sentence, "LIBRARY_MANAGER"));
        Assert.Contains("are not exported", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_types_are_listed_readably()
    {
        var report = ExportCoverage.Of(Manifest(
            Skip("LIBRARY_MANAGER", ExportCoverage.GapReason),
            Skip("CAM_TABLE", ExportCoverage.GapReason),
            Skip("SYMBOL_CONFIG", ExportCoverage.GapReason)));

        // Sorted, so the sentence does not reorder itself between exports and produce a diff
        // in a panel somebody is reading for changes.
        Assert.StartsWith("CAM_TABLE, LIBRARY_MANAGER and SYMBOL_CONFIG",
                          report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_skip_reason_counts_as_deliberate_rather_than_as_a_gap()
    {
        // Only the exporter's own gap code means a gap. A reason this build has never heard
        // of came from a newer exporter, and inventing an alarm from it would train people
        // to ignore the one that matters.
        var report = ExportCoverage.Of(Manifest(Skip("SOMETHING_NEW", "A_REASON_FROM_THE_FUTURE")));

        Assert.Equal(CoverageVerdict.CompleteByDesign, report.Verdict);
        Assert.Equal(1, report.DeliberateSkips);
    }

    [Fact]
    public void The_gaps_are_ordered_by_path_whatever_order_they_arrived_in()
    {
        var report = ExportCoverage.Of(Manifest(
            Skip("B_TYPE", ExportCoverage.GapReason, "Device/zzz"),
            Skip("A_TYPE", ExportCoverage.GapReason, "Device/aaa")));

        Assert.Equal(["Device/aaa", "Device/zzz"], report.Gaps.Select(g => g.Path));
    }

    [Fact]
    public void An_object_the_export_could_not_write_is_a_gap_and_names_itself()
    {
        // THE CASE THIS HALF EXISTS FOR, and it read Complete until 2026-08-16.
        //
        // versio_export.py warns and returns BEFORE record(), so a failed object is in
        // neither `objects` nor `skipped` - it is in `warnings` and nowhere else, and
        // nothing read `warnings` at all. So the panel said everything reached the mirror
        // while an object was missing from it, and the commit gate offered that mirror.
        var report = ExportCoverage.Of(Manifest(
            [Failure("Device/Plc Logic/Application/Deep/Thing")]));

        Assert.Equal(CoverageVerdict.HasGaps, report.Verdict);
        Assert.True(report.HasGaps);
        Assert.Empty(report.Gaps);
        Assert.Equal("Device/Plc Logic/Application/Deep/Thing",
                     Assert.Single(report.Failures).Path);
        Assert.Equal("1 object not exported", report.Label);

        var sentence = report.Describe();

        // The PATH, because a path is the identity of a failure the way a type is the
        // identity of a gap - and it is the only handle anybody has on an object that
        // reached neither the mirror nor the manifest.
        Assert.Contains("Device/Plc Logic/Application/Deep/Thing", sentence,
                        StringComparison.Ordinal);

        // Stronger than the gap wording, and the difference is the object itself: an
        // unsupported type is in the project and its CHANGES do not reach the mirror; this
        // object does not reach it at all.
        Assert.Contains("neither it nor any change to it", sentence, StringComparison.Ordinal);
        Assert.Contains("_coverage.md", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_that_is_not_a_failure_does_not_touch_the_verdict()
    {
        // The over-reporting guard, and it is not hypothetical: UNKNOWN_TYPE_GUID fires on
        // any object type this build has not been taught, which is a normal thing to meet in
        // a project and is already reported as a skip when it loses something. Counting every
        // warning as a missing object would put a red line on exports that are fine, and a
        // warning nobody believes is worse than no warning.
        var report = ExportCoverage.Of(Manifest(
            [Warning("UNKNOWN_TYPE_GUID", "Device/Plc Logic/Odd")]));

        Assert.Equal(CoverageVerdict.Complete, report.Verdict);
        Assert.False(report.HasGaps);
        Assert.Empty(report.Failures);
        Assert.Equal(string.Empty, report.Label);
    }

    [Fact]
    public void A_gap_and_a_failure_are_counted_together_and_explained_apart()
    {
        var report = ExportCoverage.Of(
            Manifest([Failure("Device/Plc Logic/Application/Thing")],
                     Skip("LIBRARY_MANAGER", ExportCoverage.GapReason)));

        // One number in the row. The reader's question is how much of their project is
        // missing, and that does not divide by the reason it is missing.
        Assert.Equal("2 objects not exported", report.Label);

        var sentence = report.Describe();

        // Two remedies, so two sentences. A gap is closed by somebody writing an exporter
        // for a type; a failure may be closed by moving the repository somewhere shallower,
        // or by nothing at all. Folding them into one line would make whichever remedy
        // was printed wrong for the other half.
        Assert.Contains("LIBRARY_MANAGER", sentence, StringComparison.Ordinal);
        Assert.Contains("will not appear in the mirror", sentence, StringComparison.Ordinal);
        Assert.Contains("Device/Plc Logic/Application/Thing", sentence, StringComparison.Ordinal);
        Assert.Contains("failed to export", sentence, StringComparison.Ordinal);

        // And the closing claim survives both, once.
        Assert.Equal(1, Occurrences(sentence, "Everything else in this project is covered."));
    }

    [Fact]
    public void Several_failures_name_the_first_and_count_the_rest()
    {
        var report = ExportCoverage.Of(Manifest(
            [Failure("Device/bbb"), Failure("Device/aaa"), Failure("Device/ccc")]));

        Assert.Equal("3 objects not exported", report.Label);

        var sentence = report.Describe();

        Assert.Contains("3 objects failed to export", sentence, StringComparison.Ordinal);

        // The first by path, not the first the script happened to emit - see the ordering
        // test below for why that is asserted separately.
        Assert.Contains("the first is Device/aaa", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("Device/ccc", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failures_are_ordered_by_path_whatever_order_they_arrived_in()
    {
        // Same rule as the gaps, and for the same reason: the script emits in walk order,
        // which changes when an object is added anywhere above it. A list that reshuffles
        // between two exports of the same project is a list nobody can diff.
        var report = ExportCoverage.Of(Manifest(
            [Failure("Device/zzz"), Failure("Device/aaa")]));

        Assert.Equal(["Device/aaa", "Device/zzz"], report.Failures.Select(f => f.Path));
    }

    [Fact]
    public void A_failure_with_no_path_still_reads_as_a_sentence()
    {
        // record() is never reached for a failure, so path is the only identity there is -
        // and warn() is called with whatever the walk had at the time. An empty one must not
        // leave the panel showing a sentence with a hole where a name should be.
        var report = ExportCoverage.Of(Manifest([Failure(string.Empty)]));

        Assert.Equal(CoverageVerdict.HasGaps, report.Verdict);
        Assert.Contains("an object the export could not name", report.Describe(),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_whose_name_could_not_be_read_is_lost_the_same_way()
    {
        // The second door, and it is one step earlier than EXPORT_FAILED: get_name() raises,
        // the walk warns and returns before `total` is even incremented, so the object is in
        // `warnings` and in nothing else — not the records, not the skips, not the count of
        // objects the export believes it walked.
        //
        // Here as its own case rather than folded into the failure test because it arrives
        // with a DIRECTORY and no name: the name is the thing that could not be read. A
        // report that could only describe a failure by naming it would have nothing to say
        // about exactly the failure that has no name.
        var report = ExportCoverage.Of(Manifest(
            [Warning(ExportCoverage.UnnamedCode, "Device/Plc Logic/Application")]));

        Assert.Equal(CoverageVerdict.HasGaps, report.Verdict);
        Assert.Equal("1 object not exported", report.Label);
        Assert.Contains("Device/Plc Logic/Application", report.Describe(),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_too_old_to_judge_claims_nothing_about_failures_either()
    {
        // The schema gate covers both lists, and that is safe rather than convenient:
        // `warnings` predates schema 2 in the exporter, so an empty one on a manifest this
        // class is willing to judge really does mean nothing failed. Below the gate no claim
        // is made at all, which is the honest answer for a manifest that may not have been
        // counting.
        var report = ExportCoverage.Of(new ProjectManifest
        {
            Schema = ExportCoverage.ReportingSchema - 1,
            Warnings = [Failure("Device/Thing")],
        });

        Assert.Equal(CoverageVerdict.Unknown, report.Verdict);
        Assert.Empty(report.Failures);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}
