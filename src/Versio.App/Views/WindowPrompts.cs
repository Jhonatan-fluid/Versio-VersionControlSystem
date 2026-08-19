using System.IO;
using System.Windows;
using Versio.App.Mvvm;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Settings;
using Versio.Core.Sync;

namespace Versio.App.Views;

/// <summary>
/// Answers the application's questions by opening a window over <paramref name="owner"/>.
/// </summary>
/// <remarks>
/// <para>
/// The real <see cref="IUserPrompts"/>. This lived as a hundred and fifteen lines of lambda
/// assignments in the middle of <c>MainWindow</c>'s constructor, which is why it is worth
/// having a name: every dialog the application can open is now in one file that says what
/// each one is for, instead of being a run of closures between the log wiring and the key
/// bindings.
/// </para>
/// <para>
/// <b>Every window is owned.</b> Not decoration: an unowned modal does not centre on the
/// application and can be lost behind it, and a modal that is blocking a close while hidden
/// behind the window it is blocking looks exactly like a hang.
/// </para>
/// <para>
/// Nothing here decides anything. Each method opens a window, waits, and returns what came
/// back — the rules about whether an operation may happen at all stay in the view model that
/// asked, which is where the repository is.
/// </para>
/// </remarks>
/// <param name="owner">The window every dialog centres on.</param>
/// <param name="shell">
/// Consulted only by <see cref="PromptForClone"/>, which hands the dialog the shell's own git
/// operations so that no dialog ever constructs a git client of its own.
/// </param>
public sealed class WindowPrompts(Window owner, ShellViewModel shell) : IUserPrompts
{
    /// <summary>
    /// Put a yes-or-no about something destructive, with the verb on the button.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was MessageBox.Show, and it was the loudest unthemed surface left in the
    /// application.</b> Every window, tooltip and popup follows the palette - WindowCaption
    /// goes as far as handing DWM the exact Surface.Base so the title bar and the top bar are
    /// one surface - and then the two most consequential prompts in the product, discarding a
    /// file and writing into a .project, opened a bright system dialog over a dark app.
    /// </para>
    /// <para>
    /// <b>Cancel leads, and that is not a style choice.</b> GuardPromptWindow gives the first
    /// option the accent and IsDefault, so putting Cancel first is what keeps a stray Enter on
    /// the safe answer - the property the MessageBox got from its defaultResult argument. The
    /// destructive button is present, quiet, and says what it does rather than "OK".
    /// </para>
    /// </remarks>
    public bool ConfirmDestructive(DestructivePrompt prompt)
    {
        var window = new GuardPromptWindow(
            prompt.Title,
            prompt.Body,
            [new PromptOption("Cancel", false), new PromptOption(prompt.Confirm, true)],
            cancels: false)
        {
            Owner = owner,
        };

        window.ShowDialog();
        return window.Result is true;
    }

    /// <summary>
    /// Ask before an import writes into a project file.
    /// </summary>
    /// <remarks>
    /// The same window as <see cref="ConfirmDestructive"/>, including the rule about which
    /// button a stray Enter lands on, and a separate method because it is a separate question:
    /// this is the one <c>RequireImportConfirmation</c> describes, and a test that counts how
    /// often the user was asked to import must not also be counting discards.
    /// </remarks>
    public bool ConfirmImport(DestructivePrompt prompt) => ConfirmDestructive(prompt);

    /// <summary>Put a guard's question, which has three answers rather than two.</summary>
    public GuardChoice? AskGuard(GuardPrompt prompt)
    {
        var window = new GuardPromptWindow(prompt) { Owner = owner };
        window.ShowDialog();
        return window.ChoiceResult;
    }

    /// <summary>Offer stashed work on returning to a branch.</summary>
    /// <remarks>
    /// Three named courses of action, so the same window the guard uses rather than a message
    /// box: "Yes" is not an answer to "restore, leave or discard".
    /// </remarks>
    public StashedWorkChoice? AskAboutStashedWork(ChoicePrompt prompt)
    {
        var window = new GuardPromptWindow(
            prompt.Title,
            prompt.Body,
            [.. prompt.Choices.Select(c => new PromptOption(AutoStashNarrative.Label(c), c))],
            cancels: StashedWorkChoice.Leave)
        {
            Owner = owner,
        };

        window.ShowDialog();
        return window.Result as StashedWorkChoice?;
    }

    /// <summary>Deal with a repository the user was told they could come back to.</summary>
    /// <remarks>
    /// Three answers with real labels, so the same window the guard uses — a message box would
    /// have to squeeze "clone it again" into Yes.
    /// </remarks>
    public MissingRepositoryChoice? AskAboutMissingRepository(MissingRepositoryPrompt prompt)
    {
        var window = new GuardPromptWindow(
            prompt.Title,
            prompt.Body,
            [.. prompt.Choices.Select(c => new PromptOption(MissingRepository.Label(c), c))])
        {
            Owner = owner,
        };

        window.ShowDialog();
        return window.Result as MissingRepositoryChoice?;
    }

    /// <summary>Ask for a branch name, validating while it is typed.</summary>
    /// <remarks>A text box and live validation, which a message box cannot do.</remarks>
    public string? PromptForBranchName()
    {
        var prompt = new BranchPromptWindow { Owner = owner };
        return prompt.ShowDialog() == true ? prompt.BranchNameResult : null;
    }

    /// <summary>Run the clone conversation and report where it landed.</summary>
    /// <remarks>
    /// The dialog runs verify-then-clone itself using git handed to it by the shell. The view
    /// supplies the window; the view model supplies the operations, so no dialog here ever
    /// constructs a git client of its own.
    /// </remarks>
    public Task<string?> PromptForClone(string? initialUrl)
    {
        var prompt = new ClonePromptWindow(
            shell.VerifyRemoteAsync,
            shell.CloneAsync,
            initialFolder: Path.GetDirectoryName(shell.RepositoryPath),
            initialUrl: initialUrl)
        {
            Owner = owner,
        };

        return Task.FromResult(prompt.ShowDialog() == true ? prompt.ClonedPath : null);
    }

    /// <summary>Collect which branches to delete and how.</summary>
    /// <remarks>
    /// A list with checkboxes and three answers, which no message box can do.
    /// </remarks>
    public DeletePlan? PromptForBranchDeletion(IReadOnlyList<BranchCandidate> candidates)
    {
        var window = new BranchDeleteWindow(candidates) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>Choose one branch to merge into the current one.</summary>
    /// <remarks>
    /// A list with one selection and a reason beside the button, which no message box can do -
    /// and the reason matters here: "already in" on a branch somebody has just pushed reads as
    /// a bug until the sentence says the work is already present.
    /// </remarks>
    public string? PromptForBranchMerge(IReadOnlyList<BranchCandidate> candidates, string into)
    {
        var window = new BranchMergeWindow(candidates, into) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>Show the copies taken before every import and return the one chosen.</summary>
    public ProjectBackup? AskWhichBackup(string project, IReadOnlyList<ProjectBackup> backups)
    {
        var window = new BackupRestoreWindow(project, backups, DateTimeOffset.UtcNow)
        {
            Owner = owner,
        };

        window.ShowDialog();
        return window.Chosen;
    }
}
