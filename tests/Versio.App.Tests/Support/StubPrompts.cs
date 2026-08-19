using Versio.App.Mvvm;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Settings;
using Versio.Core.Sync;

namespace Versio.App.Tests;

internal static class StubPromptExtensions
{
    /// <summary>The stub answering this shell's questions, attaching one if it has none.</summary>
    /// <remarks>
    /// Idempotent, so a test that answers two questions gets one stub rather than replacing
    /// the first answer with the second — which is the mistake a plain
    /// <c>shell.Prompts = new StubPrompts { ... }</c> at each site would invite.
    /// </remarks>
    public static StubPrompts Asked(this ShellViewModel shell)
    {
        if (shell.Prompts is StubPrompts already)
        {
            return already;
        }

        var stub = new StubPrompts();
        shell.Prompts = stub;
        return stub;
    }
}

/// <summary>
/// An <see cref="IUserPrompts"/> a test can answer one question at a time.
/// </summary>
/// <remarks>
/// <para>
/// The production interface has nine methods and a test almost always cares about one of
/// them, so implementing it per test would mean eight members of noise around the line that
/// matters. Each question is a settable delegate here instead, and every one left unset
/// answers exactly what <see cref="NullPrompts"/> answers — so a test that does not mention a
/// question gets the same "no" the application gives when nothing is listening.
/// </para>
/// <para>
/// That equivalence is the point, and <c>StubPromptsTests</c> pins it: a stub that answered
/// anything else would let a flow pass here and behave differently in the product.
/// </para>
/// </remarks>
internal sealed class StubPrompts : IUserPrompts
{
    public Func<DestructivePrompt, bool>? Destructive { get; set; }

    public Func<DestructivePrompt, bool>? Import { get; set; }

    public Func<GuardPrompt, GuardChoice?>? Guard { get; set; }

    public Func<ChoicePrompt, StashedWorkChoice?>? StashedWork { get; set; }

    public Func<MissingRepositoryPrompt, MissingRepositoryChoice?>? MissingRepository { get; set; }

    public Func<string?>? BranchName { get; set; }

    public Func<string?, Task<string?>>? Clone { get; set; }

    public Func<IReadOnlyList<BranchCandidate>, DeletePlan?>? BranchDeletion { get; set; }

    /// <summary>Which branch the merge picker chooses, or null for cancel.</summary>
    public Func<IReadOnlyList<BranchCandidate>, string, string?>? BranchMerge { get; set; }

    public Func<string, IReadOnlyList<ProjectBackup>, ProjectBackup?>? WhichBackup { get; set; }

    bool IUserPrompts.ConfirmDestructive(DestructivePrompt prompt) =>
        Destructive?.Invoke(prompt) ?? false;

    bool IUserPrompts.ConfirmImport(DestructivePrompt prompt) => Import?.Invoke(prompt) ?? false;

    GuardChoice? IUserPrompts.AskGuard(GuardPrompt prompt) => Guard?.Invoke(prompt);

    StashedWorkChoice? IUserPrompts.AskAboutStashedWork(ChoicePrompt prompt) =>
        StashedWork?.Invoke(prompt);

    MissingRepositoryChoice? IUserPrompts.AskAboutMissingRepository(
        MissingRepositoryPrompt prompt) => MissingRepository?.Invoke(prompt);

    string? IUserPrompts.PromptForBranchName() => BranchName?.Invoke();

    Task<string?> IUserPrompts.PromptForClone(string? initialUrl) =>
        Clone?.Invoke(initialUrl) ?? Task.FromResult<string?>(null);

    DeletePlan? IUserPrompts.PromptForBranchDeletion(IReadOnlyList<BranchCandidate> candidates) =>
        BranchDeletion?.Invoke(candidates);

    string? IUserPrompts.PromptForBranchMerge(IReadOnlyList<BranchCandidate> candidates,
                                              string into) =>
        BranchMerge?.Invoke(candidates, into);

    ProjectBackup? IUserPrompts.AskWhichBackup(string project,
                                               IReadOnlyList<ProjectBackup> backups) =>
        WhichBackup?.Invoke(project, backups);
}
