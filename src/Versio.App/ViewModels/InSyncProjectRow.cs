namespace Versio.App.ViewModels;

/// <summary>
/// One tracked project the project panel lists as calm: nothing to review, nothing to do.
/// </summary>
/// <remarks>
/// A record, so <see cref="SyncViewModel"/> can compare a fresh list against the shown one by
/// value and leave the panel alone when a refresh changed nothing.
/// </remarks>
/// <param name="Name">The project file's name, as the tree would title it.</param>
/// <param name="Path">Repo-relative path, for the tooltip.</param>
/// <param name="State">The export state in a few words — the same text the tree's row shows.</param>
public sealed record InSyncProjectRow(string Name, string Path, string State);
