using Versio.App.Mvvm;
using Versio.Core.Git;

namespace Versio.App.ViewModels;

/// <summary>
/// One stash, with its two verbs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The design the withdrawn one owed.</b> The first stash UI was six permanent controls
/// in the panel's narrowest column, for something reached a few times a month — withdrawn as
/// the wrong design, plumbing kept. This is the inversion: <i>zero</i> permanent controls.
/// The section exists only while <c>git stash list</c> has something to show, exactly as the
/// conflicts section exists only while the tree is mid-merge, and there is no "save stash"
/// button at all — the auto-stash takes stashes where Versio's own flows need one, and a
/// terminal remains the right tool for the deliberate case.
/// </para>
/// <para>
/// <b>Versio's own auto-stashes are listed too, not filtered.</b> <c>AutoStash.Marker</c>'s
/// remark is the rule: our stashes are identified by recorded SHA, never by pattern-matching
/// a message a user is free to type — and the same rule forbids hiding rows by pattern here.
/// Dropping one from this list is the same act as dropping it in a terminal, which the
/// auto-stash flow already survives by design: it is forgotten rather than offered.
/// </para>
/// <para>
/// Apply deliberately keeps the stash. <c>git stash pop</c> deletes on success and leaves a
/// half-applied mess on conflict; apply-then-drop is two decisions, each reversible until
/// the second is taken, and the Drop button is right there.
/// </para>
/// </remarks>
public sealed class StashRow
{
    public StashRow(StashEntry entry, Func<StashEntry, Task> apply, Func<StashEntry, Task> drop,
                    Func<bool> ready, Action<Exception> reportError)
    {
        Entry = entry;
        ApplyCommand = new AsyncCommand(() => apply(entry), ready, reportError);
        DropCommand = new AsyncCommand(() => drop(entry), ready, reportError);
    }

    public StashEntry Entry { get; }

    /// <summary>The stash's own message — for an auto-stash, the marker a user can read.</summary>
    public string Title => Entry.Message;

    /// <summary>Where and when, for telling two stashes with one message apart.</summary>
    public string Detail =>
        Entry.Branch.Length > 0 ? $"{Entry.Branch} · {Entry.Age}" : Entry.Age;

    public AsyncCommand ApplyCommand { get; }

    public AsyncCommand DropCommand { get; }
}
