using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A disposed shell lets go of the log console it was handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these pin.</b> <c>ShellViewModel.Dispose</c> stopped the watcher, the remote
/// poller, the sync and history panels and the repository lock — and did nothing about
/// <see cref="LogViewModel"/>, which is <see cref="IDisposable"/> because it owns a running
/// <c>DispatcherTimer</c> and a subscription to the log sink. So a test that obeyed the
/// <c>using var shell = ...</c> rule to the letter still leaked a timer and a live handler,
/// every time, for the remainder of the suite.
/// </para>
/// <para>
/// That is why it matters more than a stray timer sounds: the shell-disposal rule exists
/// because fifty-six undisposed shells made the suite fail roughly one run in three. Fixing
/// those took it to about one in fourteen, and this was the named remaining suspect — the one
/// leak the rule itself could not prevent, because <see cref="TestShell"/> constructs the log
/// and then has no way to reach it again.
/// </para>
/// <para>
/// <b>Asserted on the entry, not on the queue depth.</b> The first version of this compared
/// <c>HeldCount</c> against zero and failed for the wrong reason: the shell logs while it is
/// alive and nothing drains those entries, so the queue is two deep before the test emits
/// anything. That baseline survives the fix. What does not survive it is a specific entry,
/// emitted after disposal, arriving in the panel.
/// </para>
/// <para>
/// <b>Drained by hand, because no dispatcher pumps in a test.</b> The pump is a
/// <c>DispatcherTimer</c>, so waiting for a row is waiting forever. Un-pausing drains
/// synchronously and is the only public path that does. The filter path is not usable here
/// and the reason is worth knowing: <c>Rebuild</c> repopulates from <c>_sink.Snapshot()</c>
/// directly, so it would show the post-disposal entry whether the subscription was released
/// or not — a test written through it would pass against both versions of the code.
/// </para>
/// <para>
/// <b>Mutation checked.</b> Both cases fail against the code before the fix.
/// </para>
/// </remarks>
public sealed class ShellDisposalTests
{
    private const string AfterDisposal = "emitted after the shell was disposed";

    [Fact]
    public void An_entry_emitted_after_the_shell_is_disposed_never_reaches_the_panel()
    {
        LogViewModel log;
        InMemoryLogSink sink;

        using (var shell = TestShell.Build(out sink))
        {
            log = shell.Log;
        }

        sink.Emit(Entry(AfterDisposal));

        Drain(log);

        Assert.DoesNotContain(log.Rows, row => row.Message == AfterDisposal);
    }

    /// <summary>
    /// The same, for a log the caller built — which is how the application wires it, and how
    /// every test that does not go through <see cref="TestShell"/> wires it.
    /// </summary>
    /// <remarks>
    /// Pinned separately because it is the contract that changed. The shell is <i>handed</i>
    /// its log rather than constructing it, so disposing it is the shell taking ownership of
    /// something it did not create. That is deliberate — it is the only way one rule
    /// (<c>using var shell</c>) can cover every site — but it is exactly the kind of decision
    /// a later reader would undo as a tidy-up if nothing stated it.
    /// </remarks>
    [Fact]
    public void A_log_handed_to_the_shell_is_released_with_it()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var log = new LogViewModel(sink);

        // Without this the shell resolves real toolchains and goes looking for CODESYS on
        // whichever machine is running the suite.
        using (var shell = new ShellViewModel(logger, log, toolchains: (_, _) => null))
        {
            Assert.Same(log, shell.Log);
        }

        sink.Emit(Entry(AfterDisposal));

        Drain(log);

        Assert.DoesNotContain(log.Rows, row => row.Message == AfterDisposal);
    }

    /// <summary>Push whatever is queued into the rows, without waiting for a dispatcher.</summary>
    private static void Drain(LogViewModel log)
    {
        log.IsPaused = true;
        log.IsPaused = false;
    }

    private static LogEvent Entry(string message) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Level = LogLevel.Information,
        Category = LogCategory.App,
        Message = message,
    };
}
