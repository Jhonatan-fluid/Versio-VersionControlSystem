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
    public const int CurrentSchema = 7;

    /// <summary>The exporter's code for a skip that loses something.</summary>
    public const string GapReason = "NOT_IMPLEMENTED";

    /// <summary>The exporter's warning code for an object it could not write at all.</summary>
    /// <remarks>
    /// <para>
    /// <b>A gap the export did not decide on.</b> <see cref="GapReason"/> is a type nobody has
    /// written the code for — permanent, known in advance, and the same on every machine. This
    /// is one object on one run: the exporter reached it, tried to write it, and something
    /// threw. Path length is the trigger nobody has closed yet (G9), but a locked file or an
    /// object whose text refuses to be read arrives here too, and the list is open by
    /// construction because the <c>except</c> that produces it catches everything.
    /// </para>
    /// <para>
    /// <b>One limit, stated rather than discovered.</b> A two-file object writes and records
    /// its first half before its second, so a <c>GVL_BOTH</c> whose <c>.gvl.st</c> throws is
    /// in <c>objects</c> <i>and</i> here. Counting it as an object not exported is right —
    /// the declaration really is not in the mirror — but "1 object not exported" undercounts
    /// what survived. The alternative was to say nothing about it, which is the state this
    /// whole constant exists to end.
    /// </para>
    /// <para>
    /// <b>Why it had to be read here, and what it cost that it was not.</b> When
    /// <c>versio_export.py</c> raises on an object it warns and returns <i>before</i>
    /// <c>record()</c>, so the object lands in neither <c>objects</c> nor <c>skipped</c> — it
    /// exists in <c>warnings</c> and nowhere else. <see cref="Of"/> read <c>skipped</c> only,
    /// so the verdict came back <see cref="CoverageVerdict.Complete"/>, the panel said
    /// everything reaches the mirror, and the commit gate offered a mirror with an object
    /// missing from it. The run reported <c>Completed</c> throughout. That is the third time
    /// this class's own subject has recurred through a door it was not watching —
    /// <see cref="TaskNativeForm"/> and <see cref="GvlNativeForm"/> are the other two, and both
    /// arrived as a <i>form</i> rather than as a warning.
    /// </para>
    /// <para>
    /// <b><see cref="ProjectManifest.Warnings"/> had no reader at all</b> until this. It was
    /// parsed, typed, and documented as "what the export noticed and could not act on" — the
    /// rolling-file-sink shape, where every part of the mechanism exists except the one that
    /// makes it visible.
    /// </para>
    /// <para>
    /// It rides on <see cref="ReportingSchema"/> with the skips, and that is safe rather than
    /// convenient: <c>warnings</c> predates schema 2 in the exporter, so an empty list on any
    /// manifest this class is willing to judge means nothing failed, not that nobody was
    /// counting.
    /// </para>
    /// </remarks>
    public const string FailureCode = "EXPORT_FAILED";

    /// <summary>The exporter's warning code for an object whose name could not be read.</summary>
    /// <remarks>
    /// <para>
    /// <b>The second door, found by re-reading the exporter rather than by a test.</b> It has
    /// the identical shape to <see cref="FailureCode"/> and is one step earlier:
    /// <c>get_name()</c> raises, the walk warns and returns — before <c>total</c> is
    /// incremented, before a form is decided, before anything is recorded or skipped. So the
    /// object is in <c>warnings</c> and in no other list, and it is not even in the count of
    /// objects the export believes it walked.
    /// </para>
    /// <para>
    /// <b>The docstring three lines above it promises the opposite</b> — "unmapped type GUIDs
    /// are exported natively and warned about, never dropped: a newer CODESYS adding a type
    /// must not silently lose objects". That is true of the unmapped type on the next line and
    /// false of this one, which is why closing <see cref="FailureCode"/> alone would have
    /// repeated exactly the mistake that turned G7 into G12: patching the instance and leaving
    /// the shape. <see cref="IsFailure"/> is the shape.
    /// </para>
    /// <para>
    /// It carries a directory rather than a path, because the name is the thing that could not
    /// be read — and at the project root it carries the <b>empty string</b>, because there is
    /// no directory either. <c>Describe</c> has to survive both, and does.
    /// </para>
    /// </remarks>
    public const string UnnamedCode = "UNNAMED_OBJECT";

    /// <summary>
    /// True for a warning code meaning <i>this object is not in the mirror at all</i>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The authority on it, and deliberately the only one</b> — the same argument as
    /// <see cref="IsExportOnly"/>, which exists because two homes for one rule is how the
    /// export-only list drifted. Most of the exporter's warnings are not this: an object can
    /// be warned about and still be written, and <c>UNKNOWN_TYPE_GUID</c> is the case that
    /// proves it — it warns and then falls through to a form, so the object is exported
    /// natively and recorded. Treating every warning as a missing object would put a red line
    /// on exports that are fine, and a warning nobody believes is worse than none.
    /// </para>
    /// <para>
    /// The distinguishing shape is not the wording but the control flow: these are the codes
    /// whose <c>warn</c> is followed by a <c>return</c> out of the walk.
    /// <c>Every_exporter_warning_is_classified_as_losing_an_object_or_not</c> fails the build
    /// when the exporter grows a code this does not name, so the next one is a decision
    /// somebody makes rather than a door nobody notices.
    /// </para>
    /// </remarks>
    public static bool IsFailure(string? code) =>
        string.Equals(code, FailureCode, StringComparison.Ordinal)
        || string.Equals(code, UnnamedCode, StringComparison.Ordinal);

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
    /// The manifest's <c>form</c> for the native <c>.xml</c> half of a GVL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third export-only form, and the one that proves the pattern needed a rule rather
    /// than a habit. A GVL writes two files: <c>.gvl.st</c>, which imports, and this
    /// <c>.gvl.xml</c>, which is kept for structure and for the diff and is never applied back.
    /// </para>
    /// <para>
    /// <b>It was recorded as plain <c>NATIVE</c> until 2026-08-16 — G12.</b> Both halves were
    /// importable, and the import applies textual before native, so an edit to the
    /// <c>.gvl.st</c> reached the object and was overwritten by the XML's copy of the old
    /// declaration seconds later, while the log said <c>applied</c>. It is
    /// <see cref="TaskNativeForm"/>'s defect one object along, and it survived the fix for that
    /// one because <c>TASK_BOTH</c> was patched individually rather than the shape being given
    /// an invariant. <c>A_two_file_object_has_exactly_one_importable_half</c> is that
    /// invariant, and it runs against a real export rather than a hand-built manifest, because
    /// both defects were in what the exporter <i>wrote</i>.
    /// </para>
    /// </remarks>
    public const string GvlNativeForm = "GVL_NATIVE";

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
        || string.Equals(form, TaskNativeForm, StringComparison.Ordinal)
        || string.Equals(form, GvlNativeForm, StringComparison.Ordinal);

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

        // Read from Warnings, not from Skipped, because a failed object is in neither the
        // records nor the skips - see FailureCode for why that is the whole point, and
        // IsFailure for which codes mean it.
        var failures = manifest.Warnings
            .Where(w => IsFailure(w.Code))
            .OrderBy(w => w.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deliberate = manifest.Skipped.Count - gaps.Count;

        return new CoverageReport
        {
            // One verdict for both, and deliberately: from where the reader sits, an object
            // Versio cannot export and an object Versio does not support are the same
            // sentence - something in this project is not in the mirror. WHY they differ is
            // worth saying, so Describe says it; how loudly to say it is not.
            Verdict = gaps.Count > 0 || failures.Count > 0
                ? CoverageVerdict.HasGaps
                : deliberate > 0
                    ? CoverageVerdict.CompleteByDesign
                    : CoverageVerdict.Complete,
            Gaps = gaps,
            Failures = failures,
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

    /// <summary>
    /// Objects the export tried to write and could not. Empty unless <see cref="Verdict"/> is
    /// <see cref="CoverageVerdict.HasGaps"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Gaps"/> because the remedy is different in kind. A gap is
    /// closed by somebody writing an exporter for a type; a failure may be closed by moving
    /// the repository somewhere shallower, closing whatever holds the file open, or nothing at
    /// all if it does not recur. Folding the two into one list would have made the report
    /// shorter and its only actionable sentence wrong.
    /// </remarks>
    /// <seealso cref="ExportCoverage.FailureCode"/>
    public IReadOnlyList<ExportWarning> Failures { get; init; } = [];

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
        // Gaps and failures counted together. The row has space for one number and the
        // reader's question is "how much of my project is missing", which does not divide
        // by the reason it is missing - Describe is where that belongs.
        CoverageVerdict.HasGaps => Gaps.Count + Failures.Count == 1
            ? "1 object not exported"
            : $"{Gaps.Count + Failures.Count} objects not exported",
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
            GapClause() + FailureClause() +
            "Everything else in this project is covered. See _coverage.md.",

        CoverageVerdict.Unknown =>
            "This export was written before Versio recorded what it left out, so how much of " +
            "the project it covers cannot be stated. Export again to find out.",

        CoverageVerdict.CompleteByDesign =>
            $"Everything in this project reaches the mirror. {DeliberateSkips} service " +
            "object(s) are not exported, and nothing is lost by that — their content is " +
            "carried by the project template or by another object's export." + OneWay(),

        _ => "Everything in this project reaches the mirror." + OneWay(),
    };

    /// <summary>The sentence about unsupported types, or nothing when there are none.</summary>
    /// <remarks>
    /// Word for word what it always said. It is a clause rather than the whole sentence only
    /// so that a failure can stand beside it — the two are independent, and a report carrying
    /// both used to be impossible to state because failures were not read at all.
    /// </remarks>
    private string GapClause() =>
        Gaps.Count == 0
            ? string.Empty
            : $"{Types()} {(Gaps.Count == 1 ? "is" : "are")} not exported, so changes to " +
              $"{(Gaps.Count == 1 ? "it" : "them")} will not appear in the mirror, in a diff " +
              "or in a commit. ";

    /// <summary>The sentence about objects that failed to export, or nothing when none did.</summary>
    /// <remarks>
    /// <para>
    /// <b>Stronger than the gap clause on purpose.</b> An unsupported type is in the project
    /// and its <i>changes</i> do not reach the mirror; a failed object does not reach it at
    /// all, so neither it nor any edit to it is in a diff or a commit. Saying "changes to it"
    /// here would understate it by exactly the object.
    /// </para>
    /// <para>
    /// <b>Named, and only the first one.</b> A path is the identity of a failure the way a
    /// type is the identity of a gap — every instance of a gap is the same story about a type,
    /// and every failure is its own story about one object. One is named in full because a
    /// name is what makes it findable; the rest are a count, with <c>_coverage.md</c> carrying
    /// the list, on the same argument as the findings line in the project panel.
    /// </para>
    /// </remarks>
    private string FailureClause()
    {
        if (Failures.Count == 0)
        {
            return string.Empty;
        }

        // Path, falling back to the code. record() is never reached for these, so path is all
        // the identity that exists - and an empty one still has to read as something rather
        // than as a sentence with a hole in it.
        var first = string.IsNullOrEmpty(Failures[0].Path)
            ? "an object the export could not name"
            : Failures[0].Path;

        return Failures.Count == 1
            ? $"{first} failed to export, so neither it nor any change to it is in the " +
              "mirror, in a diff or in a commit. "
            : $"{Failures.Count} objects failed to export, so neither they nor any change to " +
              $"them are in the mirror, in a diff or in a commit — the first is {first}. ";
    }

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
