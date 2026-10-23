namespace Versio.Core.Projects;

/// <summary>
/// What the Rendered pane says for an object whose own file holds nothing to read
/// (<see cref="Plc.IPlcProjectFormat.HoldsNoSettings"/>).
/// </summary>
/// <remarks>
/// One wording for the desktop pane and the web viewer, so the two cannot drift. The second
/// sentence only when the object has children in the tree: telling a reader to look below an
/// object with nothing below it would send them looking for something that is not there.
/// </remarks>
public static class EmptyObjectNote
{
    public static string For(bool hasChildren) =>
        "This object stores no settings of its own."
        + (hasChildren ? " What it holds is drawn under it in the project tree." : "")
        + " Raw shows its file.";
}
