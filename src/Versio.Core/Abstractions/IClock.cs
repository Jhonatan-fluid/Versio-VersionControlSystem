namespace Versio.Core.Abstractions;

/// <summary>
/// Time, abstracted. Versio.Core never calls <c>DateTimeOffset.UtcNow</c> directly — the
/// architecture tests enforce it — because debounce, lease renewal and circuit-breaker
/// windows are all time-dependent and must be testable without sleeping.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Test double: time only moves when you move it.</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public FakeClock() : this(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)) { }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
