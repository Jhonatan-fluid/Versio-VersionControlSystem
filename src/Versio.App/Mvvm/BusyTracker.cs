using System.Collections.Concurrent;

namespace Versio.App.Mvvm;

/// <summary>
/// One place that knows whether Versio is doing something, and what.
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
/// newest matches what the user just did.
/// </para>
/// </remarks>
public sealed class BusyTracker : ObservableObject
{
    private readonly ConcurrentDictionary<long, string> _running = new();
    private long _next;

    /// <summary>True while at least one operation is in flight.</summary>
    public bool IsBusy => !_running.IsEmpty;

    /// <summary>What is happening, for the caption beside the bar.</summary>
    public string Message =>
        _running.IsEmpty
            ? string.Empty
            : _running.OrderByDescending(e => e.Key).First().Value;

    /// <summary>How many operations are in flight. Exposed for diagnostics and tests.</summary>
    public int Count => _running.Count;

    /// <summary>
    /// Mark the start of an operation. Dispose the result when it ends.
    /// </summary>
    /// <remarks>
    /// Returning an <see cref="IDisposable"/> rather than paired Begin/End calls means the
    /// bar cannot be left up by an early return or an exception - the <c>using</c> ends it on
    /// every path out, including the ones nobody thought about.
    /// </remarks>
    public IDisposable Begin(string what)
    {
        var id = Interlocked.Increment(ref _next);
        _running[id] = what;
        Changed();
        return new Scope(this, id);
    }

    private void End(long id)
    {
        if (_running.TryRemove(id, out _))
        {
            Changed();
        }
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
    }

    private sealed class Scope(BusyTracker owner, long id) : IDisposable
    {
        private bool _disposed;

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
