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

    private static ProjectManifest Manifest(params SkippedObject[] skipped) =>
        new()
        {
            Schema = ExportCoverage.ReportingSchema,
            Project = "Machine.project",
            Objects = [new ProjectObject { Path = "App/Main.st", Guid = "g1" }],
            Skipped = skipped,
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
                    Form = ExportCoverage.ExportOnlyForm,
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
        Assert.Contains("change those in the IDE", sentence, StringComparison.Ordinal);

        // Still no badge: this is a property of the format, not a problem with the export.
        // A row that said something on every project with a library manager — which is every
        // project — would be read past within a day.
        Assert.Equal(string.Empty, report.Label);
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
