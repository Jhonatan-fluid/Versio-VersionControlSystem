namespace Versio.Core.Projects;

/// <summary>How much of a project the export actually carries.</summary>
public enum CoverageVerdict
{
    /// <summary>The export predates coverage reporting, so nothing can be claimed.</summary>
    /// <remarks>
    /// A separate answer from <see cref="Complete"/> on purpose. An old manifest lists nothing
    /// skipped because nobody was counting, and reading that as full coverage would turn every
    /// stale export into a clean bill of health — the most comfortable possible lie, and the
    /// one nobody would think to check.
    /// </remarks>
    Unknown,

    /// <summary>Everything the project contains reaches the mirror.</summary>
    Complete,

    /// <summary>Some objects were left out deliberately, and nothing is lost by it.</summary>
    CompleteByDesign,

    /// <summary>Something the project contains does not reach the mirror at all.</summary>
    HasGaps,
}

/// <summary>
/// What the export left out, and whether that matters.
/// </summary>
/// <remarks>
/// <para>
/// Versio's claim is that git can tell you what changed in a PLC project. That claim holds
/// exactly as far as the export does, and the export has always skipped some object types —
/// most for good reasons, at least one because nobody has written the code yet. Until this
/// existed, both looked identical from the outside: absent.
/// </para>
/// <para>
/// <b>The gap that prompted it, and is now closed:</b> <c>LIBRARY_MANAGER</c> was skipped
/// while the comment beside it in the exporter claimed its content was "captured in
/// <c>config/libraries.yaml</c> instead". No code had ever written that file, so adding a
/// library or bumping a version changed nothing in the mirror, moved nothing through the hash
/// gate and appeared in no diff — silently, which is the part that made it a bug rather than a
/// limitation. Library references are exported from manifest schema 3.
/// </para>
/// <para>
/// Reported first and fixed after, in that order and deliberately. A stated gap is one
/// somebody can work around; an unstated one is a wrong answer to the only question the tool
/// exists to answer. The reporting outlives the gap that prompted it — there will be others,
/// and <c>NOT_IMPLEMENTED</c> is how the next one announces itself.
/// </para>
/// </remarks>
public static class ExportCoverage
{
    /// <summary>Manifests at or above this schema record what they skipped.</summary>
    /// <remarks>
    /// A threshold, not the current version: it is the oldest manifest whose empty skip list
    /// can be believed. It does not move when the schema does, because an export written at
    /// schema 2 still recorded honestly what <i>it</i> left out.
    /// </remarks>
    public const int ReportingSchema = 2;

    /// <summary>The schema today's exporter writes.</summary>
    /// <seealso cref="ProjectManifest.Schema"/>
    public const int CurrentSchema = 5;

    /// <summary>The exporter's code for a skip that loses something.</summary>
    public const string GapReason = "NOT_IMPLEMENTED";

    /// <summary>
    /// The manifest's <c>form</c> for library references — exported, never applied back.
    /// </summary>
    /// <remarks>
    /// A stable machine string produced by the exporter, like the skip reasons. Library
    /// references were the first of these: real project state, written so a change reaches the
    /// diff, with no import path that can be trusted. A file that looks editable and is
    /// silently ignored is its own kind of gap, so it is counted and said out loud rather than
    /// left for somebody to discover by editing one.
    /// </remarks>
    public const string LibrariesForm = "LIBRARIES";

    /// <summary>
    /// The manifest's <c>form</c> for the native <c>.xml</c> half of a task configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second export-only form, and the one that proves the first was not a special case.
    /// A task configuration writes two files: <c>.tasks.yaml</c>, which imports, and this
    /// <c>.xml</c>, which is kept for fidelity and for the diff and is never applied back.
    /// </para>
    /// <para>
    /// <b>It was recorded as <c>NATIVE_R</c> until 2026-08-15</b>, which is the form a
    /// property's recursive export carries — an importable file. So for the whole of Phase 4
    /// the mirror held a file that the import silently discarded while every channel that
    /// exists to say so reported it as ordinary: no line in <c>_coverage.md</c>, no lock glyph
    /// in the panel, nothing in the manifest. That is precisely the gap this class was written
    /// for, re-opened by the change that closed G7 and G11.
    /// </para>
    /// </remarks>
    public const string TaskNativeForm = "TASK_NATIVE";

    /// <summary>
    /// True for a manifest <c>form</c> whose file is written for review and never applied back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The authority on one-wayness, and deliberately the only one.</b> It keys off the
    /// manifest string rather than any vendor's form enum, because the panel that draws the
    /// lock glyph is vendor-neutral and a manifest is all it ever has. A CODESYS-side
    /// predicate saying the same thing existed until 2026-08-15, had no caller and no test,
    /// and was wrong — it still answered "libraries only" long after <c>TaskBoth</c>'s own
    /// documentation said its XML was export-only. Two homes for one rule is how that drifted.
    /// </para>
    /// <para>
    /// The mirror of <c>EXPORT_ONLY_FORMS</c> in <c>versio_types.py</c>, which the exporter
    /// uses for the same decision in <c>_coverage.md</c>. The two are compared in
    /// <c>TypeTableParityTests</c>: a form on one side only means the file is announced in one
    /// place and looks editable in the other, which is worse than not warning at all.
    /// </para>
    /// </remarks>
    public static bool IsExportOnly(string? form) =>
        string.Equals(form, LibrariesForm, StringComparison.Ordinal)
        || string.Equals(form, TaskNativeForm, StringComparison.Ordinal);

    /// <summary>What an export covers, from its manifest alone.</summary>
    public static CoverageReport Of(ProjectManifest? manifest)
    {
        if (manifest is null || manifest.Schema < ReportingSchema)
        {
            return new CoverageReport { Verdict = CoverageVerdict.Unknown };
        }

        var gaps = manifest.Skipped
            .Where(s => string.Equals(s.Reason, GapReason, StringComparison.Ordinal))
            .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deliberate = manifest.Skipped.Count - gaps.Count;

        return new CoverageReport
        {
            Verdict = gaps.Count > 0
                ? CoverageVerdict.HasGaps
                : deliberate > 0
                    ? CoverageVerdict.CompleteByDesign
                    : CoverageVerdict.Complete,
            Gaps = gaps,
            DeliberateSkips = deliberate,
            FilesWritten = manifest.Objects.Count,
            ExportOnlyFiles = manifest.Objects.Count(o => IsExportOnly(o.Form)),
        };
    }
}

/// <summary>The answer, with the wording that goes on screen.</summary>
public sealed record CoverageReport
{
    public required CoverageVerdict Verdict { get; init; }

    /// <summary>Objects whose changes reach nothing. Empty unless <see cref="Verdict"/> is
    /// <see cref="CoverageVerdict.HasGaps"/>.</summary>
    public IReadOnlyList<SkippedObject> Gaps { get; init; } = [];

    /// <summary>How many objects were left out for a reason that loses nothing.</summary>
    public int DeliberateSkips { get; init; }

    public int FilesWritten { get; init; }

    /// <summary>
    /// How many written files are exported for review and never applied back.
    /// </summary>
    /// <remarks>
    /// Not a gap: the content <i>is</i> in the mirror and a change to it does reach a diff and
    /// a commit. What it is not is editable from here, and the difference is worth a sentence
    /// rather than a discovery.
    /// </remarks>
    public int ExportOnlyFiles { get; init; }

    /// <summary>True when something in the project cannot be seen in the mirror.</summary>
    public bool HasGaps => Verdict == CoverageVerdict.HasGaps;

    /// <summary>
    /// A few words for a row, or empty when there is nothing worth saying.
    /// </summary>
    /// <remarks>
    /// Empty for a clean export rather than "fully covered". A row that congratulated itself
    /// on every project, every refresh, would be read past within a day — and then the one
    /// time it said something else it would be read past too.
    /// </remarks>
    public string Label => Verdict switch
    {
        CoverageVerdict.HasGaps => Gaps.Count == 1
            ? "1 object not exported"
            : $"{Gaps.Count} objects not exported",
        CoverageVerdict.Unknown => "coverage unknown",
        _ => string.Empty,
    };

    /// <summary>
    /// The full sentence, naming what is missing and what follows from it.
    /// </summary>
    /// <remarks>
    /// In Core beside the rest of the wording, so the panel, the log and the tests cannot
    /// describe the same state differently. Every phrasing here says what the reader cannot
    /// rely on, because "3 objects were skipped" is a fact nobody can act on.
    /// </remarks>
    public string Describe() => Verdict switch
    {
        CoverageVerdict.HasGaps =>
            $"{Types()} {(Gaps.Count == 1 ? "is" : "are")} not exported, so changes to " +
            $"{(Gaps.Count == 1 ? "it" : "them")} will not appear in the mirror, in a diff " +
            "or in a commit. Everything else in this project is covered. See _coverage.md.",

        CoverageVerdict.Unknown =>
            "This export was written before Versio recorded what it left out, so how much of " +
            "the project it covers cannot be stated. Export again to find out.",

        CoverageVerdict.CompleteByDesign =>
            $"Everything in this project reaches the mirror. {DeliberateSkips} service " +
            "object(s) are not exported, and nothing is lost by that — their content is " +
            "carried by the project template or by another object's export." + OneWay(),

        _ => "Everything in this project reaches the mirror." + OneWay(),
    };

    /// <summary>The sentence about one-way files, or nothing when there are none.</summary>
    /// <remarks>
    /// <para>
    /// Appended rather than woven in, so the reader sees the coverage answer first and this
    /// second. It qualifies "everything reaches the mirror" without contradicting it: the
    /// content is there, the edit direction is not.
    /// </para>
    /// <para>
    /// <b>It names no remedy, on purpose.</b> It used to say "library references … change those
    /// in the IDE", which was true while libraries were the only one-way form and became wrong
    /// the moment a task configuration's XML joined them — its remedy is the
    /// <c>.tasks.yaml</c> beside it, and sending somebody to the IDE walks them past the file
    /// they were meant to edit. The remedy is per file, so it is stated per file, in
    /// <c>_coverage.md</c> and nowhere else. A summary that restates a per-file rule is a
    /// second copy of it, and the second copy is the one that goes stale.
    /// </para>
    /// </remarks>
    private string OneWay() =>
        ExportOnlyFiles == 0
            ? string.Empty
            : $" {ExportOnlyFiles} file(s) are exported for review and not applied back; " +
              "_coverage.md names each one and what to edit instead.";

    /// <summary>The distinct CODESYS types behind the gaps, in a readable list.</summary>
    private string Types()
    {
        var types = Gaps
            .Select(g => string.IsNullOrEmpty(g.Type) ? g.Name : g.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return types.Count switch
        {
            0 => "Some objects",
            1 => types[0],
            2 => $"{types[0]} and {types[1]}",
            _ => $"{string.Join(", ", types.Take(types.Count - 1))} and {types[^1]}",
        };
    }
}
