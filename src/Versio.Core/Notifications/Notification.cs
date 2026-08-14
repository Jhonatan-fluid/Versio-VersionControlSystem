namespace Versio.Core.Notifications;

/// <summary>How much of the user's attention an event has earned.</summary>
public enum NotificationSeverity
{
    /// <summary>Something the user asked for happened. Confirmation, not news.</summary>
    Success,

    /// <summary>It worked, with a caveat worth reading once.</summary>
    Warning,

    /// <summary>It did not work.</summary>
    Failure,
}

/// <summary>
/// One thing that happened, said once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Events, as against state.</b> "3 files changed" is state: it is true until it is not, and
/// it belongs in the status bar. "Pushed" is an event: it was true for an instant and describing
/// it as a standing condition is wrong. Versio held both on one property with one lifetime, and
/// the result was that <see cref="Severity"/> Success events were overwritten by the status
/// refresh that followed them — a successful push produced no visible confirmation at all.
/// </para>
/// <para>
/// <b>The identity is <see cref="Headline"/>, not a generated id.</b> De-duplication needs to
/// recognise the same event happening again, and what makes two notifications "the same" to a
/// reader is that they say the same thing. A counter would make every retry of a failing push
/// a fresh card and build a wall of identical text.
/// </para>
/// </remarks>
public sealed record Notification
{
    public required NotificationSeverity Severity { get; init; }

    /// <summary>The one line the user reads. Also the de-duplication key.</summary>
    public required string Headline { get; init; }

    /// <summary>
    /// Git's own words, when there are any.
    /// </summary>
    /// <remarks>
    /// Kept verbatim and unabridged: <c>rejected — non-fast-forward</c> and
    /// <c>Authentication failed</c> are the lines that get pasted into a message asking a
    /// colleague for help, and a paraphrase is useless for that.
    /// </remarks>
    public string Detail { get; init; } = string.Empty;

    public required DateTimeOffset PostedUtc { get; init; }

    /// <summary>
    /// When this stops being shown, or null for one that never does.
    /// </summary>
    /// <remarks>
    /// Null is <see cref="NotificationSeverity.Failure"/> and only that. A failed push is the
    /// single most important thing this panel can say, and a failure that quietly vanished
    /// after four seconds is how somebody goes home believing their work is on the remote.
    /// </remarks>
    public DateTimeOffset? ExpiresUtc { get; init; }

    public bool IsSticky => ExpiresUtc is null;

    /// <summary>Whether this has outlived its welcome by <paramref name="now"/>.</summary>
    public bool HasExpired(DateTimeOffset now) => ExpiresUtc is { } when && now >= when;

    /// <summary>Both lines, for the clipboard.</summary>
    /// <remarks>
    /// One string rather than two, because what somebody wants on the clipboard is the whole
    /// message. Detail alone loses what was being attempted; headline alone loses git's reason.
    /// </remarks>
    public string ToClipboardText() =>
        Detail.Length == 0 ? Headline : $"{Headline}{Environment.NewLine}{Detail}";
}
