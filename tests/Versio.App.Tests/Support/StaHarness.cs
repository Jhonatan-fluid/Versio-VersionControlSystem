using System.Windows;
using System.Windows.Threading;

namespace Versio.App.Tests;

/// <summary>
/// Runs work on the one STA thread that owns the theme.
/// </summary>
/// <remarks>
/// <para>
/// Three things are needed before a WPF control can be constructed in a test, and each one
/// fails in a way that names something other than the cause:
/// </para>
/// <list type="number">
///   <item><b>An STA thread.</b> xunit runs tests on MTA threads, where a <c>Window</c>
///     constructor throws.</item>
///   <item><b>An <see cref="Application"/> carrying the theme.</b> Every <c>StaticResource</c>
///     in a dialog resolves by walking up to <c>Application.Resources</c>; without one the
///     XAML parse throws on the first style it cannot find.</item>
///   <item><b>The SAME STA thread every time.</b> This is the one that is not obvious. The
///     brushes in the theme are <c>Freezable</c>s owned by the thread that created them, so a
///     fresh STA thread per test dies inside the XAML parser with
///     <c>Dispatcher.VerifyAccess</c> in the stack and nothing pointing at the real problem —
///     which is what the first version of this harness did.</item>
/// </list>
/// <para>
/// So there is one long-lived STA thread running a <see cref="Dispatcher"/>, work is marshalled
/// onto it, and the <see cref="Application"/> is built there once. It is a background thread,
/// so it never keeps the test host alive. Nothing here shows a window.
/// </para>
/// <para>
/// The theme is loaded <b>by pack URI from the app assembly</b> rather than rebuilt here, which
/// is the point of the whole harness: a style renamed in the real stylesheet and not in a
/// dialog fails at build time instead of the first time somebody opens that dialog.
/// </para>
/// </remarks>
internal static class StaHarness
{
    private static readonly Lazy<Dispatcher> UiThread = new(Start, isThreadSafe: true);

    /// <summary>Run <paramref name="work"/> on the UI thread, rethrowing what it throws.</summary>
    public static void Run(Action work) => UiThread.Value.Invoke(work);

    /// <summary>
    /// Run asynchronous <paramref name="work"/> on the UI thread and wait for it to finish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed as soon as a test clicks a button whose handler awaits anything. On the UI
    /// thread an <c>await</c> posts its continuation back to the dispatcher, so work started
    /// inside a plain <see cref="Run(Action)"/> is only <i>begun</i> by the time that call
    /// returns — the rest happens whenever the dispatcher next pumps, which is during some
    /// later test. That is not a hypothetical: it aborted the whole test host.
    /// </para>
    /// <para>
    /// Waiting from the CALLING thread rather than the dispatcher thread is what makes this
    /// safe. The dispatcher stays free to pump its own continuations; blocking on it from
    /// inside itself would deadlock instead.
    /// </para>
    /// </remarks>
    public static void Run(Func<Task> work) =>
        UiThread.Value.InvokeAsync(work).Task.Unwrap().GetAwaiter().GetResult();

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var application = Application.Current ?? new Application();

                // WPF shuts an Application down when its last window closes, and that default
                // is fatal here: one test that shows a window and closes it again would take
                // the harness with it, and every test after it would fail inside
                // InitializeComponent with "The Application object is being shut down" —
                // naming the victim rather than the cause.
                //
                // Nothing in this suite wants the application to exit. It is a background
                // thread on a host that ends when the run does.
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // The app's own dictionary, by pack URI, so this cannot drift from what ships.
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/Versio;component/Theme/Theme.xaml",
                        UriKind.Absolute),
                });

                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                // Reported to whoever is waiting rather than thrown into a thread nobody
                // joins, which would take the host down with no attribution.
                ready.SetException(ex);
                return;
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Versio.App.Tests UI",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }
}
