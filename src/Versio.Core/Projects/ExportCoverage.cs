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
/// <b>The gap that prompted it:</b> <c>LIBRARY_MANAGER</c> is skipped, and the comment beside
/// it in the exporter claimed its content was "captured in <c>config/libraries.yaml</c>
/// instead". No code has ever written that file. Adding a library or bumping a version
/// therefore changes nothing in the mirror, moves nothing through the hash gate, and appears
/// in no diff — silently, which is the part that makes it a bug rather than a limitation.
/// </para>
/// <para>
/// Reported rather than fixed, in that order and deliberately. A stated gap is one somebody
/// can work around; an unstated one is a wrong answer to the only question the tool exists to
/// answer.
/// </para>
/// </remarks>
public static class ExportCoverage
{
    /// <summary>Manifests at or above this schema record what they skipped.</summary>
    public const int ReportingSchema = 2;

    /// <summary>The exporter's code for a skip that loses something.</summary>
    public const string GapReason = "NOT_IMPLEMENTED";

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
            "carried by the project template or by another object's export.",

        _ => "Everything in this project reaches the mirror.",
    };

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
