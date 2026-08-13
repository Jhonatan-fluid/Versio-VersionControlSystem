namespace Versio.Core.Settings;

/// <summary>What can be done about a recent repository that is no longer there.</summary>
public enum MissingRepositoryChoice
{
    /// <summary>Leave the entry alone and open nothing. Always offered, always last.</summary>
    Cancel,

    /// <summary>Clone it back from the remote it was cloned from, to the same path.</summary>
    CloneAgain,

    /// <summary>Take it out of the list. The folder is not touched.</summary>
    Forget,
}

/// <summary>Why a remembered repository could not be opened.</summary>
public enum MissingRepositoryReason
{
    /// <summary>Nothing at the path at all.</summary>
    Gone,

    /// <summary>The folder is there and git will not work in it — the clone was deleted
    /// from inside, or the folder was reused for something else.</summary>
    NotARepository,
}

/// <summary>The question to ask, and the answers worth offering.</summary>
public sealed record MissingRepositoryPrompt(
    string Title, string Body, IReadOnlyList<MissingRepositoryChoice> Choices);

/// <summary>
/// Turns a remembered repository that has vanished into a question.
/// </summary>
/// <remarks>
/// <para>
/// Pure and in Core for the same reason <see cref="Guarding.GuardNarrative"/> is: which
/// options exist is a decision, not a rendering detail, and the decision here is entirely
/// about whether a remote was recorded. Offering <i>Clone again</i> for a repository that
/// never had one would be a button that cannot work.
/// </para>
/// <para>
/// <b>Nothing here deletes a folder.</b> <see cref="MissingRepositoryChoice.Forget"/> removes
/// a row from a list; the closest this gets to the filesystem is asking git to clone into a
/// path that is empty. A dropdown is not where anybody expects to lose a directory, and a
/// missing folder is often a disconnected drive rather than a deleted clone.
/// </para>
/// </remarks>
public static class MissingRepository
{
    public static MissingRepositoryPrompt Prompt(
        RecentRepository entry, MissingRepositoryReason reason)
    {
        var hasRemote = entry.RemoteUrl.Length > 0;

        var what = reason switch
        {
            MissingRepositoryReason.NotARepository =>
                $"{entry.Path}\n\nThe folder is still there, but it is not a Git repository " +
                "any more.",
            _ => $"{entry.Path}\n\nThere is nothing at that path now. It may have been moved " +
                 "or deleted, or it may be on a drive that is not connected.",
        };

        var offer = hasRemote
            ? $"\n\nVersio remembers where it came from:\n{entry.RemoteUrl}"

            // Said out loud rather than left as a missing button. "Why is there no clone
            // option" is a question the dialog should not make somebody ask.
            : "\n\nNo remote was recorded for it, so there is nothing to clone it back from.";

        return new MissingRepositoryPrompt(
            reason == MissingRepositoryReason.NotARepository
                ? "No longer a repository"
                : "That repository is gone",
            what + offer,
            hasRemote
                ? [MissingRepositoryChoice.CloneAgain, MissingRepositoryChoice.Forget,
                   MissingRepositoryChoice.Cancel]
                : [MissingRepositoryChoice.Forget, MissingRepositoryChoice.Cancel]);
    }

    /// <summary>The words on the button.</summary>
    public static string Label(MissingRepositoryChoice choice) => choice switch
    {
        MissingRepositoryChoice.CloneAgain => "Clone it again",

        // "Remove", never "Delete". Deleting is what the user is afraid this does, and the
        // label is the only place that fear gets answered before they click.
        MissingRepositoryChoice.Forget => "Remove from list",
        _ => "Cancel",
    };
}
