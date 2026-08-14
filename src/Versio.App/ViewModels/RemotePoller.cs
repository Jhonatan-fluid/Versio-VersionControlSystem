using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Sync;

namespace Versio.App.ViewModels;

/// <summary>
/// Keeps the ahead/behind counts honest without anybody asking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Ahead and behind are the reason people open the git panel, and they
/// are only ever as true as the last fetch. Every gating rule built on them — whether Push is
/// live, what the badge says, whether a merge request can be opened — is otherwise decided from
/// knowledge that could be hours old.
/// </para>
/// <para>
/// <b>It probes; it does not fetch.</b> <c>ls-remote</c> asks what the remote's branch points
/// at and returns one line with no object transfer. Only when that differs from the local
/// remote-tracking ref does a real fetch run. The steady state is one small round trip; the
/// expensive path runs when there is something new to learn.
/// </para>
/// <para>
/// <b>It never pulls, and that constraint is load-bearing.</b> A pull rewrites the working tree
/// — both the project and its mirror — under the watcher, which is the one scenario
/// <c>SAFEGUARDS</c> §5.A calls able to corrupt a project through no user error at all. A
/// fetch moves refs and objects inside <c>.git</c> and leaves every working file alone.
/// Automatic pulling would be that corruption on a timer.
/// </para>
/// <para>
/// <b>Four rules keep it out of the way</b>, and each was chosen against a specific way this
/// could go wrong:
/// </para>
/// <list type="number">
///   <item><b>It never raises the <see cref="BusyTracker"/>.</b> Every git button consults that
///     flag, so a probe that raised it would make the whole panel flicker dead every few
///     seconds — the same reasoning that already keeps automatic status refreshes off the
///     progress bar.</item>
///   <item><b>It is skipped entirely while anything else is running.</b> A probe competing for
///     credentials and network during a push is pure noise. Checked before spawning
///     <i>and</i> before applying a result, so one that started first cannot land mid-push.</item>
///   <item><b>A probe that finds nothing changes nothing.</b> No property is raised, no list is
///     rebuilt, no diff is reloaded. Zero pixels move.</item>
///   <item><b>An authentication failure stops it on the first occurrence</b>, not after the
///     usual run. Retrying a rejected credential every few seconds cannot succeed and may lock
///     an account.</item>
/// </list>
/// <para>
/// The pace lives in <see cref="RemotePollPolicy"/> and is tested there against a fake clock.
/// What is here is the loop, the suspension checks and the marshalling.
/// </para>
/// </remarks>
public sealed class RemotePoller : IDisposable
{
    private readonly IVersioLogger _logger;
    private readonly IClock _clock;
    private readonly RemotePollPolicy _policy;
    private readonly QuietRunTracker _quiet = new();

    private readonly Func<IGitClient?> _client;
    private readonly Func<bool> _suspended;
    private readonly Func<(string Remote, string Branch)?> _target;
    private readonly Func<CancellationToken, Task> _onMoved;
    private readonly Action<string> _onHalted;

    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>
    /// How long the loop sleeps between asking the policy whether a probe is due.
    /// </summary>
    /// <remarks>
    /// A second, which is finer than any interval the policy will produce. The loop ticks;
    /// the policy decides. Splitting it that way is what lets the interval change underneath a
    /// running loop without cancelling and restarting anything.
    /// </remarks>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <param name="client">The open repository's client, or null when none is open.</param>
    /// <param name="suspended">
    /// True while the poller must not run: another git operation, or a modal question waiting
    /// for an answer.
    /// </param>
    /// <param name="target">Which remote and branch to watch, or null when there is nothing.</param>
    /// <param name="onMoved">Run a real fetch and refresh. Called on the UI context.</param>
    /// <param name="onHalted">Say once that polling has stopped, and why.</param>
    /// <param name="onProbed">
    /// Anything else worth asking the remote while the connection is warranted anyway.
    /// </param>
    public RemotePoller(
        IVersioLogger logger,
        IClock clock,
        Func<IGitClient?> client,
        Func<bool> suspended,
        Func<(string Remote, string Branch)?> target,
        Func<CancellationToken, Task> onMoved,
        Action<string> onHalted,
        TimeSpan? interval = null,
        Func<CancellationToken, Task>? onProbed = null)
    {
        _logger = logger;
        _clock = clock;
        _client = client;
        _suspended = suspended;
        _target = target;
        _onMoved = onMoved;
        _onHalted = onHalted;
        _onProbed = onProbed;
        _policy = new RemotePollPolicy(clock, interval);
    }

    /// <summary>
    /// A second question for the remote, asked on this loop's schedule rather than its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The merge-request button needs to know whether a request already exists, which is a
    /// <c>ls-remote</c> against a hidden ref namespace. It gets this loop's cadence, backoff and
    /// suspension for free, and — crucially — its halt: a rejected credential stops both.
    /// </para>
    /// <para>
    /// <b>It runs whether or not the remote moved, and it has to.</b> Opening a pull request
    /// does not move the branch head — the ref the forge publishes points at a commit that
    /// already exists — so a probe conditioned on movement would never notice one being opened.
    /// The caller decides whether the question is worth asking at all; this only says when.
    /// </para>
    /// </remarks>
    private readonly Func<CancellationToken, Task>? _onProbed;

    /// <summary>Exposed so the shell can wake it and a test can read the pace.</summary>
    public RemotePollPolicy Policy => _policy;

    /// <summary>
    /// Whether a caller gave this loop a second question to ask.
    /// </summary>
    /// <remarks>
    /// <b>Here so that "the shell forgot to wire it" is a failing test rather than a silence.</b>
    /// A test of this class can prove the callback is invoked, and a test of the shell can prove
    /// the merge-request rules are right, and both stay green with <c>onProbed: null</c> passed
    /// at the one construction site — which is the shape of the three unreachable-caller bugs
    /// this codebase has already had. Internal because nothing in the application asks.
    /// </remarks>
    internal bool AsksASecondQuestion => _onProbed is not null;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Start watching. Idempotent.</summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        _logger.Info(LogCategory.Git, "watching the remote for changes");
    }

    /// <summary>Stop watching, and wait for the loop to notice.</summary>
    public void Stop()
    {
        if (_stop is null)
        {
            return;
        }

        _stop.Cancel();
        _stop.Dispose();
        _stop = null;
        _loop = null;
    }

    /// <summary>Something happened that makes the remote worth watching closely again.</summary>
    public void Wake() => _policy.Wake();

    /// <summary>Resume after the user has dealt with whatever stopped it.</summary>
    public void Resume()
    {
        _policy.Resume();
        _logger.Info(LogCategory.Git, "resuming the remote watch");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, ct).ConfigureAwait(false);
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A background loop that dies silently leaves the counts stale for ever, with
                // nothing on screen or in the log to say why. It is logged and the loop
                // continues; the policy's failure counting decides when to give up.
                _logger.Error(LogCategory.Git, "the remote watch hit an error", ex);
                _policy.Record(ProbeOutcome.Failed, ex.Message);
                ReportHaltIfNeeded();
            }
        }
    }

    /// <summary>One turn of the loop. Internal so a test can drive it without a timer.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        if (_suspended() || !_policy.ShouldProbe(_clock.UtcNow))
        {
            return;
        }

        if (_client() is not { } git || _target() is not { } target)
        {
            return;
        }

        var started = _clock.UtcNow;
        var remoteHead = await git.LsRemoteAsync(target.Remote, target.Branch, ct)
            .ConfigureAwait(false);

        // Between the probe starting and finishing, the user may have pressed something. A
        // fetch landing in the middle of their push is exactly what the suspension is for.
        if (_suspended() || ct.IsCancellationRequested)
        {
            return;
        }

        var elapsed = (_clock.UtcNow - started).TotalMilliseconds;

        if (remoteHead.Length == 0)
        {
            // Either the branch is not on the remote — ordinary for one never published — or
            // the call failed. Both mean there is nothing to compare, and neither is worth a
            // notification on a timer.
            _policy.Record(ProbeOutcome.Failed, "the remote did not answer");
            _logger.Debug(LogCategory.Git,
                $"probe {target.Remote}/{target.Branch}: no answer in {elapsed:F0} ms");
            ReportHaltIfNeeded();
            return;
        }

        // The remote answered, so the connection and the credentials are good. Anything else
        // worth asking it is asked here, before the unchanged path returns — a pull request
        // being opened does not move the branch head, so a question asked only when something
        // moved would never be answered.
        if (_onProbed is not null)
        {
            await _onProbed(ct).ConfigureAwait(false);
        }

        var localHead = await git.RevParseAsync($"{target.Remote}/{target.Branch}", ct)
            .ConfigureAwait(false);

        if (string.Equals(remoteHead, localHead, StringComparison.OrdinalIgnoreCase))
        {
            _policy.Record(ProbeOutcome.Unchanged);

            // Every trigger is logged, and the log stays readable: the per-probe line is Debug,
            // and a run of them is collapsed into one periodic sentence. A 10 s poll writing a
            // line each is 360 an hour, which is how the log panel got drowned before.
            _logger.Debug(LogCategory.Git,
                $"probe {target.Remote}/{target.Branch}: unchanged in {elapsed:F0} ms");

            if (_quiet.RecordQuiet("remote", _clock.UtcNow) is { } summary)
            {
                _logger.Info(LogCategory.Git, summary);
            }

            return;
        }

        if (_quiet.RecordChange(_clock.UtcNow) is { } ended)
        {
            _logger.Debug(LogCategory.Git, ended);
        }

        // The line somebody looks for when asking "when did the remote change".
        _logger.Info(LogCategory.Git,
            $"{target.Remote}/{target.Branch} moved: {Short(localHead)} -> {Short(remoteHead)}");

        _policy.Record(ProbeOutcome.Moved);

        // The only expensive call, and only now that there is something to learn.
        await _onMoved(ct).ConfigureAwait(false);
    }

    /// <summary>Note an authentication refusal, which stops the loop at once.</summary>
    /// <remarks>
    /// Called by the shell when a real operation classifies a failure as terminal. The poller's
    /// own probes cannot tell the difference — <c>ls-remote</c> returning nothing looks the
    /// same whether the remote refused us or was simply unreachable — so the richer answer
    /// comes from the operations that have git's full stderr.
    /// </remarks>
    public void NoteRefused(string reason)
    {
        if (_policy.Halted)
        {
            return;
        }

        _policy.Record(ProbeOutcome.Refused, reason);
        ReportHaltIfNeeded();
    }

    private void ReportHaltIfNeeded()
    {
        if (_policy.Halted)
        {
            _logger.Warn(LogCategory.Git, $"remote watch stopped: {_policy.HaltReason}");
            _onHalted(_policy.HaltReason);
        }
    }

    private static string Short(string commit) =>
        commit.Length >= 7 ? commit[..7] : commit.Length == 0 ? "(none)" : commit;

    public void Dispose() => Stop();
}
