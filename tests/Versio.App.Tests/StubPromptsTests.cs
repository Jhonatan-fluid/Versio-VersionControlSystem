using Versio.App.Mvvm;
using Versio.Core.Guarding;
using Versio.Core.Settings;
using Versio.Core.Updates;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// An unanswered question in a test is answered the way the application answers it.
/// </summary>
/// <remarks>
/// <para>
/// Almost every test that uses <see cref="StubPrompts"/> sets one of the nine delegates and
/// leaves eight unset, so what those eight return is the behaviour most of the suite actually
/// runs against. If the stub were more permissive than <see cref="NullPrompts"/> — a
/// <c>ConfirmDestructive</c> defaulting to true, say — a flow would pass here and do something
/// else entirely in the product, and no test would be looking at the difference.
/// </para>
/// <para>
/// So the two are pinned together rather than merely written to match. This is cheap and it
/// is the kind of drift nothing else would catch: both files are edited by whoever adds a
/// tenth question, and only one of them is exercised by a failing test if they disagree.
/// </para>
/// </remarks>
public sealed class StubPromptsTests
{
    private static readonly DestructivePrompt Anything = new("Title", "Body");

    [Fact]
    public void An_unset_stub_refuses_everything_the_null_prompts_refuse()
    {
        IUserPrompts stub = new StubPrompts();
        IUserPrompts none = NullPrompts.Instance;

        Assert.Equal(none.ConfirmDestructive(Anything), stub.ConfirmDestructive(Anything));
        Assert.Equal(none.ConfirmImport(Anything), stub.ConfirmImport(Anything));
        Assert.Equal(none.PromptForBranchName(), stub.PromptForBranchName());
        Assert.Equal(none.PromptForBranchDeletion([]), stub.PromptForBranchDeletion([]));
        Assert.Equal(none.AskWhichBackup("p", []), stub.AskWhichBackup("p", []));
    }

    [Fact]
    public void An_unset_stub_gives_no_answer_to_the_questions_that_have_choices()
    {
        IUserPrompts stub = new StubPrompts();

        // Null rather than a choice, at every one of these. The call sites turn it into
        // cancel, or leave, or "keep the file" — which is the reason none of them may be
        // given an answer here.
        Assert.Null(stub.AskGuard(new GuardPrompt("t", "b", [])));
        Assert.Null(stub.AskAboutStashedWork(new ChoicePrompt("t", "b", [])));
        Assert.Null(stub.AskAboutMissingRepository(
            new MissingRepositoryPrompt("t", "b", [])));

        // Null is Later. An unset stub that answered Update now would download and close the
        // shell in any test that happened to run a launch check.
        Assert.Null(stub.OfferUpdate(new UpdatePrompt("0.4.4", "0.5.0", string.Empty)));
        Assert.Null(NullPrompts.Instance.OfferUpdate(new UpdatePrompt("0.4.4", "0.5.0", string.Empty)));
    }

    [Fact]
    public async Task An_unset_stub_clones_nothing()
    {
        // The one question that answers asynchronously, and the one whose unset value is a
        // Task rather than a value — so a null delegate must still produce a task to await
        // rather than a null reference.
        IUserPrompts stub = new StubPrompts();

        Assert.Null(await stub.PromptForClone(null));
        Assert.Null(await stub.PromptForClone("https://example.invalid/repo.git"));
    }
}
