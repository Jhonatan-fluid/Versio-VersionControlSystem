using System.Collections.Concurrent;
using Versio.Core.Abstractions;
using Versio.Core.Guarding;
using Versio.Core.Logging;

namespace Versio.App.Mvvm;

/// <summary>
/// One place that knows whether Versio is doing something, what, and whether it may be stopped.
/// </summary>
/// <remarks>
/// <para>
/// Long operations here are genuinely long: a CODESYS export is 17-31 seconds, a clone is
/// unbounded. Without a single visible signal the window simply sits there, and "nothing is
/// happening" and "everything is happening" look identical - which is how a user ends up
/// pressing a button a second time and launching a second toolchain run.
/// </para>
/// <para>
/// <b>Counted, not a boolean.</b> A status refresh can overlap an export, and whichever
/// finished first would clear a plain flag while the other was still running. The bar goes
/// away when the last operation ends, not the first.
/// </para>
/// <para>
/// The message is whatever started most recently. Showing the oldest would leave a 20-second
/// export's caption up while three quick refreshes came and went behind it; showing the
/// newest matches what the user just did. <see cref="Snapshot"/> orders the other way round on
/// purpose - it answers "what is holding me up", which is the longest-running thing, not the
/// latest.
/// </para>
/// <para>
/// <b>It also owns the cancellation.</b> Every scope carries its own token, so a call site that
/// has opened one cannot then forget to pass it - the token is already in the variable it is
/// holding. Before this, the plumbing to cancel a CODESYS run existed all the way down to
/// <c>KillTree</c> and every caller in the app passed <see cref="CancellationToken.None"/>, so
/// the feature was complete and unreachable.
/// </para>
/// </remarks>
/// <param name="logger">
/// Optional, defaulting to <see cref="NullLogger.Instance"/> rather than to null so no call
/// site below has to check — the same arrangement the sync orchestrator uses, and for the same
/// reason: the tests that construct this need not mention it.
/// </param>
public sealed class BusyTracker(IClock clock, IVersioLogger? logger = null) : ObservableObject
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private readonly ConcurrentDictionary<long, Entry> _running = new();
    private long _next;

    private sealed record Entry(
        WorkKind Kind,
        string What,
        DateTimeOffset StartedUtc,
        CancellationTokenSource Cancellation,
        bool OwnsCancellation);

    /// <summary>True while at least one operation is in flight.</summary>
    public bool IsBusy => !_running.IsEmpty;

    /// <summary>What is happening, for the caption beside the bar.</summary>
    public string Message =>
        _running.IsEmpty
            ? string.Empty
            : _running.OrderByDescending(e => e.Key).First().Value.What;

    /// <summary>How many operations are in flight. Exposed for diagnostics and tests.</summary>
    public int Count => _running.Count;

    /// <summary>
    /// True while anything running must be allowed to finish.
    /// </summary>
    /// <remarks>
    /// Bound by the repository and branch dropdowns, so the half of the problem that has no
    /// safe answer is unreachable rather than merely refused after the click. Raised as a
    /// property change alongside <see cref="IsBusy"/>, because a scope opening or closing can
    /// move it without moving the count in a way any binding would notice.
    /// </remarks>
    public bool HasUninterruptible =>
        _running.Any(e => WorkKinds.InterruptionOf(e.Value.Kind) == Interruption.MustFinish);

    /// <summary>
    /// Everything running, as the guard needs to see it.
    /// </summary>
    /// <remarks>
    /// A copy, taken at one instant. <see cref="InterruptionGuard"/> is pure and must give the
    /// same answer twice for the same input; handing it a live view of a concurrent dictionary
    /// would make that quietly untrue the moment an export finished mid-decision.
    /// </remarks>
    public WorkSnapshot Snapshot()
    {
        var now = clock.UtcNow;

        return new WorkSnapshot(
        [
            .. _running.Values.Select(e =>
                new ActiveWork(e.Kind, e.What, now - e.StartedUtc)),
        ]);
    }

    /// <summary>
    /// Mark the start of an operation. Dispose the result when it ends.
    /// </summary>
    /// <remarks>
    /// Returning an <see cref="IDisposable"/> rather than paired Begin/End calls means the
    /// bar cannot be left up by an early return or an exception - the <c>using</c> ends it on
    /// every path out, including the ones nobody thought about.
    /// </remarks>
    /// <param name="kind">What sort of work this is. Decides, through
    /// <see cref="WorkKinds.InterruptionOf"/>, whether it may ever be cancelled.</param>
    /// <param name="what">The caption for the bar, reused verbatim by any dialog that has to
    /// name this operation.</param>
    /// <param name="external">
    /// A token source somebody else already owns, for the case where the work started before
    /// the scope did.
    /// <para>
    /// This is not a convenience. An automatic export is begun by the watcher, and the scope
    /// for it is opened from the orchestrator's <c>RunStarted</c> event — <i>after</i> the call
    /// that started the run, and deliberately so, since the overwhelming majority of changes
    /// are refused by a gate in milliseconds and a bar flashing for each would be worse than
    /// none. A scope that minted its own token there would hand back one connected to nothing,
    /// and "stop it and continue" would silently do nothing for the single most common case of
    /// something running.
    /// </para>
    /// <para>
    /// The tracker cancels an external source but never disposes it: it did not create it, and
    /// the owner's <c>using</c> is still the thing that ends its life.
    /// </para>
    /// </param>
    public BusyScope Begin(WorkKind kind, string what, CancellationTokenSource? external = null)
    {
        var id = Interlocked.Increment(ref _next);
        var entry = new Entry(
            kind, what, clock.UtcNow,
            external ?? new CancellationTokenSource(),
            OwnsCancellation: external is null);

        _running[id] = entry;
        Changed();
        return new BusyScope(this, id, entry.Cancellation.Token);
    }

    /// <summary>
    /// Cancel everything that is allowed to be cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Work that must finish is skipped here, not merely absent.</b> The guard already
    /// refuses to ask this question while an import is running, so in practice nothing
    /// uninterruptible is ever in the dictionary when this is called — but "in practice" is
    /// not the standard for the one method in the app that can kill a CODESYS process tree.
    /// The filter is the guarantee; the guard is the courtesy.
    /// </para>
    /// <para>
    /// Cancelling is a request, not an event. The operations end when their own
    /// <c>await</c> unwinds, and the scopes come off the moment each <c>using</c> exits.
    /// </para>
    /// </remarks>
    /// <returns>How many operations were asked to stop.</returns>
    public int CancelCancellable()
    {
        var cancelled = 0;

        foreach (var entry in _running.Values)
        {
            if (WorkKinds.InterruptionOf(entry.Kind) == Interruption.MustFinish)
            {
                _log.Debug(LogCategory.App,
                    $"leaving '{entry.What}' alone; {entry.Kind} work must be allowed to finish");
                continue;
            }

            // The source is disposed by the scope that owns it, and a race between this and
            // that dispose is a normal outcome rather than a failure.
            try
            {
                entry.Cancellation.Cancel();
                cancelled++;

                // By name, at Warning. If somebody comes back asking why their export did not
                // produce a diff, this line is the answer - the same reason discarding a file
                // and dropping a stash are both logged by name.
                _log.Warn(LogCategory.App, $"cancelled '{entry.What}' on request");
            }
            catch (ObjectDisposedException)
            {
                // It finished on its own between the snapshot and here. Nothing to stop.
            }
        }

        return cancelled;
    }

    /// <summary>
    /// Completes once nothing is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What "wait for it to finish" is made of. <c>Window.Closing</c> is synchronous and cannot
    /// await, so the close is vetoed, this is awaited, and the window is closed again once it
    /// completes — which is the standard shape for an asynchronous answer to a synchronous
    /// question, and the reason the flag exists on the window.
    /// </para>
    /// <para>
    /// <b>The re-check after subscribing is not belt and braces.</b> Operations end on
    /// threadpool threads, so the last one can finish between the <see cref="IsBusy"/> test and
    /// the subscription — and the notification for it would then already have been raised to
    /// nobody. Waiting on that would hang until something unrelated happened to start and stop,
    /// which on an idle app is never.
    /// </para>
    /// </remarks>
    public Task WhenIdleAsync(CancellationToken ct = default)
    {
        if (!IsBusy)
        {
            return Task.CompletedTask;
        }

        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IsBusy) && !IsBusy)
            {
                PropertyChanged -= OnChanged;
                idle.TrySetResult();
            }
        }

        PropertyChanged += OnChanged;

        // See the remarks: it may have gone idle while we were subscribing.
        if (!IsBusy)
        {
            PropertyChanged -= OnChanged;
            idle.TrySetResult();
        }

        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                PropertyChanged -= OnChanged;
                idle.TrySetCanceled(ct);
            });
        }

        return idle.Task;
    }

    /// <summary>
    /// Cancel everything, including work that must otherwise be allowed to finish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For Windows shutting down, and nothing else.</b> The process is going to die in a few
    /// seconds whatever anybody prefers, and the choice is not between finishing the import and
    /// stopping it — it is between stopping it deliberately and leaving a hidden CODESYS
    /// orphaned, still holding the project, while its parent disappears.
    /// </para>
    /// <para>
    /// Cancelling gets the runner to <c>KillTree</c> the process it started. That is a bad
    /// outcome for a half-written project — the backup taken before the import is what makes it
    /// survivable — but it is the better of the two bad outcomes available, and it is the only
    /// moment in the application where <see cref="CancelCancellable"/>'s guarantee is
    /// deliberately set aside.
    /// </para>
    /// </remarks>
    /// <returns>How many operations were asked to stop.</returns>
    public int CancelEverythingForShutdown()
    {
        var cancelled = 0;

        foreach (var entry in _running.Values)
        {
            try
            {
                entry.Cancellation.Cancel();
                cancelled++;

                // Error, not Warning, for the ones that should never have been stopped. This
                // is the line somebody reads after a shutdown left a project that will not
                // open, and it has to say plainly that it happened and to what.
                _log.Log(
                    WorkKinds.InterruptionOf(entry.Kind) == Interruption.MustFinish
                        ? LogLevel.Error
                        : LogLevel.Warning,
                    LogCategory.App,
                    $"the session is ending: stopped '{entry.What}' mid-flight");
            }
            catch (ObjectDisposedException)
            {
                // Finished on its own. Nothing to stop.
            }
        }

        return cancelled;
    }

    private void End(long id)
    {
        if (!_running.TryRemove(id, out var entry))
        {
            return;
        }

        // Only the sources this tracker minted. Disposing a caller's would pull the token out
        // from under an operation that is still using it.
        if (entry.OwnsCancellation)
        {
            entry.Cancellation.Dispose();
        }

        Changed();
    }

    /// <remarks>
    /// Operations start and finish on background threads. WPF marshals a property-changed
    /// notification for a simple property, so this needs no dispatcher of its own - and it
    /// deliberately touches no collection the UI binds to.
    /// </remarks>
    private void Changed()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasUninterruptible));
    }

    /// <summary>One running operation, and the token that stops it.</summary>
    /// <remarks>
    /// A named type rather than a bare <see cref="IDisposable"/> so the token travels with the
    /// scope. The call site writes <c>using var busy = Busy.Begin(…)</c> and then passes
    /// <c>busy.Token</c> — there is no separate thing to remember, which is the only reason to
    /// expect this to be passed consistently when <see cref="CancellationToken.None"/> was
    /// passed consistently before.
    /// </remarks>
    public sealed class BusyScope(BusyTracker owner, long id, CancellationToken token)
        : IDisposable
    {
        private bool _disposed;

        /// <summary>Cancelled when this operation is asked to stop.</summary>
        public CancellationToken Token { get; } = token;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.End(id);
        }
    }
}
