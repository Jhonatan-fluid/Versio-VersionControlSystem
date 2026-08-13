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
