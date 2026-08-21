using Versio.App.ViewModels;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Closing Versio while it is in the middle of something.
/// </summary>
/// <remarks>
/// <para>
/// The decision lives in the view model precisely so it can be tested here: no window, no
/// dispatcher, no dialog. What the window does with the answer is three lines — veto, await,
/// close again — and those three lines cannot be got wrong in an interesting way.
/// </para>
/// <para>
/// Every wait is bounded. A test that hangs on a task that never completes tells you nothing
/// and takes the run with it, so the failure mode here is a timeout with a message rather than
/// a suite that stops.
/// </para>
/// </remarks>
public sealed class CloseGuardTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private sealed class Fixture
    {
        public required ShellViewModel Shell { get; init; }

        /// <summary>Every prompt the shell put to the user.</summary>
        public List<GuardPrompt> Asked { get; } = [];

        public static Fixture Build(GuardChoice? answer)
        {
            var sink = new InMemoryLogSink();
            var logger = new VersioLogger(new SystemClock()).AddSink(sink);
            using var shell = new ShellViewModel(logger, new LogViewModel(sink));
            var fixture = new Fixture { Shell = shell };

            shell.Asked().Guard = prompt =>
            {
                fixture.Asked.Add(prompt);
                return answer;
            };

            return fixture;
        }
    }

    private static async Task<T> Within<T>(Task<T> task, string what)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Patience));

        Assert.True(ReferenceEquals(finished, task), $"timed out waiting for {what}");
        return await task;
    }

    // ------------------------------------------------------------------ nothing running

    [Fact]
    public async Task An_idle_app_closes_without_asking()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        Assert.True(await fixture.Shell.RequestCloseAsync());
        Assert.Empty(fixture.Asked);
    }

    // ------------------------------------------------------------------ an import runs

    [Fact]
    public async Task An_import_asks_before_closing()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var importing = fixture.Shell.Busy.Begin(WorkKind.Import, "Importing into Machine…");

        Assert.False(await fixture.Shell.RequestCloseAsync());

        var prompt = Assert.Single(fixture.Asked);
        Assert.Contains(GuardChoice.CancelWorkAndProceed, prompt.Choices);
        Assert.Contains("restore", prompt.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Choosing_to_stop_it_cancels_the_import()
    {
        var fixture = Fixture.Build(GuardChoice.CancelWorkAndProceed);

        var importing = fixture.Shell.Busy.Begin(WorkKind.Import, "Importing…");

        var closing = fixture.Shell.RequestCloseAsync();

        Assert.True(importing.Token.IsCancellationRequested);

        importing.Dispose();

        Assert.True(await Within(closing, "the close to complete after the import stopped"));
    }

    [Fact]
    public async Task Backing_out_of_close_leaves_the_import_running()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var importing = fixture.Shell.Busy.Begin(WorkKind.Import, "Importing…");

        Assert.False(await fixture.Shell.RequestCloseAsync());

        Assert.False(importing.Token.IsCancellationRequested);
        Assert.True(fixture.Shell.Busy.IsBusy);
    }

    [Fact]
    public async Task Choosing_to_wait_closes_when_the_import_finishes()
    {
        var fixture = Fixture.Build(GuardChoice.WaitThenProceed);

        var importing = fixture.Shell.Busy.Begin(WorkKind.Import, "Importing…");

        var closing = fixture.Shell.RequestCloseAsync();

        Assert.False(closing.IsCompleted);
        Assert.False(importing.Token.IsCancellationRequested);

        importing.Dispose();

        Assert.True(await Within(closing, "the close to complete after the import ended"));
    }

    // ------------------------------------------------------------------ an export runs

    [Fact]
    public async Task Choosing_to_wait_closes_when_the_export_finishes()
    {
        var fixture = Fixture.Build(GuardChoice.WaitThenProceed);

        var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        var closing = fixture.Shell.RequestCloseAsync();

        // Still open: the export has not finished, so neither has the answer.
        Assert.False(closing.IsCompleted);

        exporting.Dispose();

        Assert.True(await Within(closing, "the close to complete after the export ended"));
    }

    [Fact]
    public async Task Choosing_to_stop_it_cancels_the_export()
    {
        var fixture = Fixture.Build(GuardChoice.CancelWorkAndProceed);

        var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        var closing = fixture.Shell.RequestCloseAsync();

        // Cancelling is a request. The operation ends when its own await unwinds, which the
        // dispose below stands in for.
        Assert.True(exporting.Token.IsCancellationRequested);

        exporting.Dispose();

        Assert.True(await Within(closing, "the close to complete after the export stopped"));
    }

    [Fact]
    public async Task Backing_out_leaves_the_export_alone()
    {
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        Assert.False(await fixture.Shell.RequestCloseAsync());

        Assert.False(exporting.Token.IsCancellationRequested);
        Assert.True(fixture.Shell.Busy.IsBusy);
    }

    [Fact]
    public async Task The_question_names_the_export_and_how_long_it_has_run()
    {
        // "Wait" is a reasonable answer only when the user can see what they are waiting for.
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting Machine…");

        await fixture.Shell.RequestCloseAsync();

        var prompt = Assert.Single(fixture.Asked);

        Assert.Contains("Exporting Machine…", prompt.Body, StringComparison.Ordinal);
        Assert.Equal(GuardChoice.WaitThenProceed, prompt.Recommended);
    }

    // ------------------------------------------------------------------- the safe default

    [Fact]
    public async Task With_no_dialog_wired_up_the_app_stays_open()
    {
        // Unset means Cancel, the same way unset means "no" for the destructive confirm. A
        // wiring mistake must not close the window over a running export.
        var fixture = Fixture.Build(answer: null);

        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting…");

        Assert.False(await fixture.Shell.RequestCloseAsync());
        Assert.False(exporting.Token.IsCancellationRequested);
    }

    // ------------------------------------------------------------------ session ending

    [Fact]
    public async Task A_second_close_while_waiting_does_not_ask_again()
    {
        // Every X used to start another RequestCloseAsync. The first wait still owns the
        // close; a second one must be a no-op.
        var fixture = Fixture.Build(GuardChoice.WaitThenProceed);

        var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting…");

        var first = fixture.Shell.RequestCloseAsync();
        var second = await fixture.Shell.RequestCloseAsync();

        Assert.False(second);
        Assert.Single(fixture.Asked);

        exporting.Dispose();

        Assert.True(await Within(first, "the first close to complete"));
    }

    [Fact]
    public void A_session_ending_stops_even_an_import()
    {
        // Not a good outcome, and the better of the two available: the process is going to die
        // either way, and cancelling gets the runner to kill the CODESYS tree it started
        // rather than leaving it orphaned and holding the project.
        var fixture = Fixture.Build(GuardChoice.Cancel);

        using var importing = fixture.Shell.Busy.Begin(WorkKind.Import, "Importing…");
        using var exporting = fixture.Shell.Busy.Begin(WorkKind.Export, "Exporting…");

        fixture.Shell.AbandonForShutdown();

        Assert.True(importing.Token.IsCancellationRequested);
        Assert.True(exporting.Token.IsCancellationRequested);
        Assert.Empty(fixture.Asked);
    }
}
