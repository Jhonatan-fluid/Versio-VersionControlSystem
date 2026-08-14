using Versio.Core.Abstractions;

namespace Versio.Core.Sync;

/// <summary>What a probe of the remote found.</summary>
public enum ProbeOutcome
{
    /// <summary>The remote is where we last saw it.</summary>
    Unchanged,

    /// <summary>The remote has moved. Worth a real fetch.</summary>
    Moved,

    /// <summary>It could not be reached. Worth trying again later.</summary>
    Failed,

    /// <summary>
    /// It refused us. Trying again cannot help.
    /// </summary>
    /// <remarks>
    /// A rejected credential, a missing one, or an account without access. Retrying any of
    /// those every few seconds cannot succeed and may lock an account, which is considerably
    /// worse than a stale count.
    /// </remarks>
    Refused,
}

/// <summary>
/// How often to ask the remote whether anything has changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The panel's counts are only ever as true as the last fetch.</b> Ahead and behind are what
/// people open this panel to see, and without something keeping them fresh every gating rule
/// built on them is decided from possibly-stale knowledge. This is that something.
/// </para>
/// <para>
/// <b>The frequent call is deliberately not a fetch.</b> A fetch opens a connection, negotiates
/// and transfers objects. The cheap question — has the remote moved? — is
/// <c>git ls-remote</c>, which returns one line and no objects, and a real fetch runs only when
/// the answer has actually changed. So the steady state costs one small round trip and the
/// expensive path runs only when there is something new to learn.
/// </para>
/// <para>
/// <b>Adaptive rather than fixed, because git has no connection reuse.</b> Every probe is a
/// process spawn plus a TLS handshake and an auth round trip. At a flat few seconds that is
/// well over a thousand an hour per open window — fine against a LAN server, enough to attract
/// attention from a hosted one. So a run of quiet probes stretches the interval, and anything
/// interesting snaps it straight back: somebody actively working sees near-live counts, and a
/// window left open overnight stops hammering a remote for nobody.
/// </para>
/// <para>
/// Pure and clocked by the caller, so every interval and every backoff step is asserted without
/// waiting a single second.
/// </para>
/// </remarks>
public sealed class RemotePollPolicy(
    IClock clock, TimeSpan? baseInterval = null, TimeSpan? floor = null)
{
    /// <summary>
    /// How long between probes while something is happening.
    /// </summary>
    /// <remarks>
    /// <b>Ten seconds is a starting point, not a measurement.</b> The right value is a
    /// property of the remote — a LAN git server and a hosted one over HTTPS differ by an
    /// order of magnitude — and it should be set from a timed <c>ls-remote</c> against the
    /// real thing rather than from this comment.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    /// <summary>The shortest interval that will ever be used, however it is configured.</summary>
    /// <remarks>
    /// Five seconds. Below that the process spawns start to matter on a slow machine, and the
    /// answer is not fresher in any way a person can perceive.
    /// </remarks>
    public static readonly TimeSpan DefaultFloor = TimeSpan.FromSeconds(5);

    /// <summary>The longest a quiet run stretches to.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    /// <summary>How many quiet probes before the interval stretches a step.</summary>
    /// <remarks>
    /// Six. At the ten-second base that is a minute of nothing happening before the poll
    /// slows, which is long enough not to punish somebody who paused to read a diff.
    /// </remarks>
    public const int QuietProbesPerStep = 6;

    private readonly TimeSpan _base = Clamp(baseInterval ?? DefaultInterval, floor);
    private readonly TimeSpan _floor = floor ?? DefaultFloor;

    private DateTimeOffset? _lastProbe;
    private int _quietRun;
    private int _consecutiveFailures;

    /// <summary>True once something refused us and only a user action can change that.</summary>
    public bool Halted { get; private set; }

    /// <summary>Why it halted, in git's words, for the one notification that says so.</summary>
    public string HaltReason { get; private set; } = string.Empty;

    /// <summary>How many probes in a row have found nothing.</summary>
    public int QuietRun => _quietRun;

    /// <summary>
    /// How long until the next probe, given how quiet it has been.
    /// </summary>
    /// <remarks>
    /// Doubling per step rather than a fixed ladder, so the shape holds if the base interval is
    /// changed. Clamped at both ends: never below the floor, never above the ceiling.
    /// </remarks>
    public TimeSpan CurrentInterval
    {
        get
        {
            var steps = _quietRun / QuietProbesPerStep;
            var scaled = TimeSpan.FromTicks(_base.Ticks * (long)Math.Pow(2, Math.Min(steps, 8)));

            return scaled > Ceiling ? Ceiling : scaled < _floor ? _floor : scaled;
        }
    }

    /// <summary>Whether it is time to ask the remote again.</summary>
    public bool ShouldProbe(DateTimeOffset now) =>
        !Halted && (_lastProbe is not { } last || now - last >= CurrentInterval);

    /// <summary>
    /// Record what a probe found, and adjust the pace.
    /// </summary>
    /// <remarks>
    /// <b>A refusal halts immediately rather than after a run of failures.</b> The circuit
    /// breaker's threshold is for things that might recover — a laptop on a train, a VPN
    /// coming back — and a rejected credential is not one of them.
    /// </remarks>
    public void Record(ProbeOutcome outcome, string reason = "")
    {
        _lastProbe = clock.UtcNow;

        switch (outcome)
        {
            case ProbeOutcome.Unchanged:
                _quietRun++;
                _consecutiveFailures = 0;
                break;

            case ProbeOutcome.Moved:
                _quietRun = 0;
                _consecutiveFailures = 0;
                break;

            case ProbeOutcome.Failed:
                // Not quiet — something is wrong — but not a reason to poll harder either.
                _consecutiveFailures++;
                if (_consecutiveFailures >= FailuresBeforeHalt)
                {
                    Halt(reason.Length > 0 ? reason : "the remote could not be reached");
                }

                break;

            case ProbeOutcome.Refused:
                Halt(reason.Length > 0 ? reason : "the remote refused us");
                break;
        }
    }

    /// <summary>How many unreachable probes before giving up.</summary>
    /// <remarks>
    /// Five, matching <see cref="CircuitBreaker"/>'s default. An offline laptop otherwise logs
    /// a failure every ten seconds for as long as it stays shut.
    /// </remarks>
    public const int FailuresBeforeHalt = 5;

    private void Halt(string reason)
    {
        Halted = true;
        HaltReason = reason;
    }

    /// <summary>
    /// Something happened that makes the remote worth watching closely again.
    /// </summary>
    /// <remarks>
    /// Called when the user touches the git panel, when any git command finishes, and when a
    /// probe finds movement. The point of the backoff is to stop polling for <i>nobody</i>;
    /// the moment there is somebody, it should be as live as it was at the start.
    /// </remarks>
    public void Wake()
    {
        _quietRun = 0;

        // Deliberately does NOT clear Halted. A halt is a state a user has to resolve — a
        // credential, a network — and quietly resuming on a mouse movement would hide it and
        // start the hammering again.
    }

    /// <summary>Resume after the user has dealt with whatever stopped it.</summary>
    public void Resume()
    {
        Halted = false;
        HaltReason = string.Empty;
        _quietRun = 0;
        _consecutiveFailures = 0;
        _lastProbe = null;
    }

    private static TimeSpan Clamp(TimeSpan interval, TimeSpan? floor)
    {
        var min = floor ?? DefaultFloor;
        return interval < min ? min : interval;
    }
}
