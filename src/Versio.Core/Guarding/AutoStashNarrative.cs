namespace Versio.Core.Guarding;

/// <summary>What to do with work Versio stashed when you left this branch.</summary>
public enum StashedWorkChoice
{
    /// <summary>Leave it stashed. Offered again next time, and the default when nobody answers.</summary>
    Leave,

    /// <summary>Put it back in the working tree, with the sync state it had.</summary>
    Restore,

    /// <summary>Throw it away. Behind a confirmation, and irreversible.</summary>
    Discard,
}

/// <summary>
/// The words for the offer made on returning to a branch.
/// </summary>
/// <remarks>
/// <para>
/// In Core beside the rules, not in the dialog, for the reason every other narrative here is:
/// what is said and what is decided are the same thing said twice, and a window that composed
/// its own sentences would eventually describe a state the policy no longer produces.
/// </para>
/// <para>
/// <b>The wording answers "how much is at stake" before it asks anything.</b> The count of
/// files and how long ago are what somebody needs to recognise the work as theirs — a dialog
/// that says only "you have a stash" is asking them to guess.
/// </para>
/// </remarks>
public static class AutoStashNarrative
{
    /// <summary>The offer, when there is something to offer.</summary>
    public static ChoicePrompt Arrival(StashArrival arrival, DateTimeOffset now)
    {
        var offer = arrival.Restorable[0].Record;
        var files = offer.FileCount == 1 ? "1 changed file" : $"{offer.FileCount} changed files";

        var body = $"Versio saved {files} when you switched away from {offer.Branch} " +
                   $"{Ago(now - offer.CreatedUtc)}.";

        // Said only when it is true, and it is worth saying: switching away twice without
        // coming back is legitimate, and somebody who is offered one stash while holding three
        // would reasonably assume the other two are gone.
        if (arrival.Restorable.Count > 1)
        {
            body += $"\n\nThere are {arrival.Restorable.Count} saved for this branch. This is " +
                    "the most recent; the others are offered after it.";
        }

        return new ChoicePrompt(
            "Stashed work for this branch",
            body,
            [StashedWorkChoice.Restore, StashedWorkChoice.Leave, StashedWorkChoice.Discard]);
    }

    /// <summary>The confirmation shown before a stash is thrown away.</summary>
    /// <remarks>
    /// Names the count and the branch, because <c>git stash drop</c> cannot be undone and the
    /// only defence against dropping the wrong one is recognising it in the sentence.
    /// </remarks>
    public static (string Title, string Body) DiscardWarning(LiveStash offer)
    {
        var files = offer.Record.FileCount == 1
            ? "1 changed file"
            : $"{offer.Record.FileCount} changed files";

        return ("Discard the stashed work?",
                $"{files} saved on {offer.Record.Branch} will be deleted.\n\n" +
                "Git cannot bring a dropped stash back.");
    }

    /// <summary>The label on the button.</summary>
    public static string Label(StashedWorkChoice choice) => choice switch
    {
        StashedWorkChoice.Restore => "Restore it",
        StashedWorkChoice.Discard => "Discard it",
        _ => "Leave it stashed",
    };

    /// <summary>
    /// How long ago, in the terms somebody actually thinks in.
    /// </summary>
    /// <remarks>
    /// Written here rather than taken from git's <c>%cr</c>, because the record's own timestamp
    /// is what this is about — git would date the stash <i>commit</i>, which is the same thing
    /// today and not after a record outlives its stash and is re-created.
    /// </remarks>
    private static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 90 } => "a moment ago",
        { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} minutes ago",
        { TotalHours: < 2 } => "an hour ago",
        { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} hours ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)elapsed.TotalDays} days ago",
    };
}

/// <summary>A question with named answers, in the shape the prompt window renders.</summary>
/// <remarks>
/// Deliberately not <see cref="GuardPrompt"/>: that one carries <see cref="GuardChoice"/>,
/// which is about interrupting work in flight, and reusing it would make "restore my files" an
/// answer to "something is still running". Same window, different question.
/// </remarks>
public sealed record ChoicePrompt(
    string Title, string Body, IReadOnlyList<StashedWorkChoice> Choices);
