namespace Versio.Core.Git;

/// <summary>
/// The revision ranges behind "what am I about to push" and "what is about to arrive".
/// </summary>
/// <remarks>
/// <para>
/// Trivial string-building, kept in Core anyway, because each shape is an easy one to get
/// silently backwards: <c>upstream..HEAD</c> and <c>HEAD..upstream</c> both produce a
/// plausible list, and a preview that showed the <i>other</i> direction's commits would be
/// read and believed. The tests here are the statement of which way round each one goes.
/// </para>
/// <para>
/// <b>Null when there is no upstream, never a guess.</b> A branch with no upstream has no
/// "about to push" — publish is a different operation with its own button — and asking git
/// with a bare <c>..HEAD</c> would answer with every commit in the repository.
/// </para>
/// </remarks>
public static class GitRevisionRange
{
    /// <summary>Commits this branch has that the upstream lacks — what a push would send.</summary>
    public static string? Outgoing(string? upstream) =>
        string.IsNullOrWhiteSpace(upstream) ? null : $"{upstream}..HEAD";

    /// <summary>Commits the upstream has that this branch lacks — what a pull would bring.</summary>
    public static string? Incoming(string? upstream) =>
        string.IsNullOrWhiteSpace(upstream) ? null : $"HEAD..{upstream}";

    /// <summary>
    /// The file difference a pull would apply, as a <c>diff</c> range.
    /// </summary>
    /// <remarks>
    /// Three dots, not two, and the distinction is the safety half of the preview: a
    /// three-dot diff compares the upstream against the <b>merge base</b>, so it lists only
    /// what the other side changed. A two-dot diff compares the two tips directly, which on
    /// a diverged branch counts this machine's own unpushed edits as "arriving" — objects
    /// about to be imported that nobody else touched.
    /// </remarks>
    public static string? IncomingDiff(string? upstream) =>
        string.IsNullOrWhiteSpace(upstream) ? null : $"HEAD...{upstream}";
}
