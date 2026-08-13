using Versio.Core.Git;

namespace Versio.App.ViewModels;

/// <summary>
/// One letter and one colour for what happened to a file.
/// </summary>
/// <remarks>
/// Shared rather than written out per list, for the same reason the view has a single
/// <c>ChangeRowTemplate</c>: two copies of this mapping would be two chances for a deletion
/// to be red in one panel and amber in the next. The template draws whatever it is given, so
/// keeping the mapping in one place is what actually makes the lists agree.
/// <para>
/// The project tree deliberately does <i>not</i> use this. Its colour answers a different
/// question — the roll-up of everything beneath a folder, where an untracked file counts as
/// an addition — and folding the two together would make one of them wrong.
/// </para>
/// </remarks>
public static class ChangeGlyph
{
    public static string For(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Modified => "M",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.Untracked => "?",
        ChangeKind.Conflicted => "!",
        _ => " ",
    };

    public static string BrushFor(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Brush.Status.Added",
        ChangeKind.Modified => "Brush.Status.Modified",
        ChangeKind.Deleted => "Brush.Status.Deleted",
        ChangeKind.Renamed => "Brush.Status.Renamed",
        ChangeKind.Conflicted => "Brush.Status.Conflict",
        _ => "Brush.Status.Neutral",
    };
}
