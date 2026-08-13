using Versio.Core.Settings;

namespace Versio.Core.Abstractions;

/// <summary>
/// Where this machine's Versio preferences live.
/// </summary>
/// <remarks>
/// Behind an interface for the same reason <c>ILockStore</c> is: the rules about the recent
/// list are worth testing without a disk, and a view model that reached for a file path
/// directly could not be constructed in a test at all.
/// </remarks>
public interface IAppSettingsStore
{
    /// <summary>
    /// Read the settings, or <see cref="AppSettings.Empty"/> when there are none.
    /// </summary>
    /// <remarks>
    /// <b>Never throws.</b> An unreadable settings file must cost a dropdown's contents and
    /// nothing else — this is read on the way to showing a window, and a corrupt preference
    /// taking the app down with it would be the worst possible trade for a convenience.
    /// </remarks>
    Task<AppSettings> ReadAsync(CancellationToken ct);

    /// <summary>Persist the settings, replacing whatever was there.</summary>
    Task WriteAsync(AppSettings settings, CancellationToken ct);
}

/// <summary>
/// A store that remembers nothing.
/// </summary>
/// <remarks>
/// <b>The default for a view model, so that constructing one touches no disk.</b> It is not a
/// convenience: the real store writes to <c>%LOCALAPPDATA%</c>, and defaulting to it meant a
/// test that opened a temp repository quietly rewrote the developer's own recent list — which
/// is exactly what happened, with two <c>versio-reset-…</c> temp folders landing in it. The
/// application host wires the real store; anything else gets this one and behaves normally
/// within its own session.
/// </remarks>
public sealed class NullAppSettingsStore : IAppSettingsStore
{
    public static readonly NullAppSettingsStore Instance = new();

    public Task<AppSettings> ReadAsync(CancellationToken ct) =>
        Task.FromResult(AppSettings.Empty);

    public Task WriteAsync(AppSettings settings, CancellationToken ct) => Task.CompletedTask;
}
