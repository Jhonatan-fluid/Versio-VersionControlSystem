using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Settings;
using Versio.Core.Sync;

namespace Versio.App.Mvvm;

/// <summary>
/// Every question the application puts to the user, in one place.
/// </summary>
/// <remarks>
/// <para>
/// These were nine settable <c>Func&lt;&gt;</c> properties spread across two view models and
/// wired from a hundred and fifteen lines in the middle of <c>MainWindow</c>'s constructor.
/// Nothing was wrong with any one of them; what was wrong was that the set of questions the
/// app can ask had no name and could not be read anywhere. Adding a tenth meant adding a
/// nullable mutable property to a view model and remembering the wiring.
/// </para>
/// <para>
/// <b>A view model never opens a window.</b> That is the rule these exist to keep, and the
/// reason is the same for all nine: a modal opened from a view model blocks whichever thread
/// happened to call it, and makes the command that opened it untestable. The view model
/// decides that a question is needed and what it says; the view decides what it looks like.
/// </para>
/// <para>
/// <b>Null is a real answer, and it means "no" every time.</b> Each method that can return
/// null returns it both when the user backs out of the dialog and when there is no view
/// attached at all — so <see cref="NullPrompts"/> needs no policy of its own, and a host that
/// forgets to supply prompts destroys nothing rather than silently taking the destructive
/// branch. The call sites map that one answer onto whatever "no" means for them: cancel a
/// switch, leave a stash where it is, keep a file.
/// </para>
/// </remarks>
public interface IUserPrompts
{
    /// <summary>Ask before destroying work git cannot give back. False means don't.</summary>
    /// <remarks>
    /// A prompt rather than two strings because the button's words are part of the question.
    /// These were answered by a message box, so every one of them ended in OK and Cancel
    /// while the captions were phrased as questions — "Discard changes to this file?". OK is
    /// not an answer to that.
    /// </remarks>
    bool ConfirmDestructive(DestructivePrompt prompt);

    /// <summary>
    /// Ask before an import writes into a project file. False means don't.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="ConfirmDestructive"/> rather than folded into it: this is the
    /// setting <c>RequireImportConfirmation</c> describes, and the two want to be countable
    /// apart when a test asks which question was put.
    /// </remarks>
    bool ConfirmImport(DestructivePrompt prompt);

    /// <summary>Put a guard's question to the user. Null means cancel.</summary>
    /// <remarks>
    /// Two or three named courses of action, so a real dialog rather than a message box:
    /// "Yes" is not an answer to "stash, discard or cancel".
    /// </remarks>
    GuardChoice? AskGuard(GuardPrompt prompt);

    /// <summary>Offer stashed work on returning to a branch. Null means leave it.</summary>
    /// <remarks>
    /// Leave is the only one of the three answers that cannot lose anything, which is why it
    /// is what no answer means.
    /// </remarks>
    StashedWorkChoice? AskAboutStashedWork(ChoicePrompt prompt);

    /// <summary>
    /// Decide what to do about a remembered repository that has gone. Null means cancel.
    /// </summary>
    MissingRepositoryChoice? AskAboutMissingRepository(MissingRepositoryPrompt prompt);

    /// <summary>Ask for a new branch name. Null means the user backed out.</summary>
    /// <remarks>
    /// The dialog validates while typing with <see cref="BranchName"/>; the view model
    /// validates the answer again, because one that trusts its view has no way to state what
    /// it requires.
    /// </remarks>
    string? PromptForBranchName();

    /// <summary>
    /// Run the clone conversation, returning where the clone landed, or null.
    /// </summary>
    /// <remarks>
    /// The dialog drives verify-then-clone itself, because "Clone stays disabled until Verify
    /// passes" is a rule about a conversation rather than about one operation. What comes back
    /// is a path on disk, which is all the shell needs to open it.
    /// </remarks>
    /// <param name="initialUrl">
    /// Prefilled when the clone is a repository being fetched back rather than a new one. The
    /// dialog still requires Verify to pass: a remembered URL is a convenience, not a claim
    /// that the remote is still there.
    /// </param>
    Task<string?> PromptForClone(string? initialUrl);

    /// <summary>Collect which branches to delete and how. Null means cancel.</summary>
    /// <remarks>
    /// The dialog collects the choice; every rule about whether it may happen — the guard, the
    /// remote confirmation, the checkout off a branch being deleted — stays in the view model
    /// where the repository is.
    /// </remarks>
    DeletePlan? PromptForBranchDeletion(IReadOnlyList<BranchCandidate> candidates);

    /// <summary>Choose a branch to merge into the current one. Null means cancel.</summary>
    /// <remarks>
    /// One choice rather than a set, unlike the deletion prompt beside it: merging two branches
    /// in one gesture is two merges with one confirmation, and the second would land on a tree
    /// the user has not looked at.
    /// </remarks>
    string? PromptForBranchMerge(IReadOnlyList<BranchCandidate> candidates, string into);

    /// <summary>Choose which saved copy of a project to restore. Null means cancel.</summary>
    /// <remarks>
    /// <b>Choosing is the confirmation.</b> There is no second "are you sure" on top: the
    /// window states what restoring does and what it keeps, and picking a named version from a
    /// list is already a deliberate act. A dialog on top of a dialog is how people learn to
    /// dismiss both without reading either — which is why this is the one destructive-looking
    /// operation here that does not also go through <see cref="ConfirmDestructive"/>.
    /// </remarks>
    ProjectBackup? AskWhichBackup(string project, IReadOnlyList<ProjectBackup> backups);
}

/// <summary>
/// The prompts a view model gets when nothing is listening: every question answered "no".
/// </summary>
/// <remarks>
/// <para>
/// The default rather than a null <see cref="IUserPrompts"/>, so no call site needs a null
/// check and no future one can forget it. It is also what every test that does not care about
/// a particular question gets, which is why "no" has to be the safe answer to all of them
/// rather than merely a plausible one.
/// </para>
/// <para>
/// Nothing here decides anything. Each method returns the same "no answer" a user gets by
/// closing the dialog, and the call site turns that into whatever not answering means there —
/// so the behaviour with no view attached is the behaviour of a user who cancels everything,
/// stated once instead of nine times.
/// </para>
/// </remarks>
public sealed class NullPrompts : IUserPrompts
{
    public static readonly NullPrompts Instance = new();

    private NullPrompts()
    {
    }

    public bool ConfirmDestructive(DestructivePrompt prompt) => false;

    public bool ConfirmImport(DestructivePrompt prompt) => false;

    public GuardChoice? AskGuard(GuardPrompt prompt) => null;

    public StashedWorkChoice? AskAboutStashedWork(ChoicePrompt prompt) => null;

    public MissingRepositoryChoice? AskAboutMissingRepository(MissingRepositoryPrompt prompt) =>
        null;

    public string? PromptForBranchName() => null;

    public Task<string?> PromptForClone(string? initialUrl) => Task.FromResult<string?>(null);

    public DeletePlan? PromptForBranchDeletion(IReadOnlyList<BranchCandidate> candidates) => null;

    public string? PromptForBranchMerge(IReadOnlyList<BranchCandidate> candidates, string into) =>
        null;

    public ProjectBackup? AskWhichBackup(string project, IReadOnlyList<ProjectBackup> backups) =>
        null;
}
