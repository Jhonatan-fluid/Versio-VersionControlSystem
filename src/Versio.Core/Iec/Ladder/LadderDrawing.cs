namespace Versio.Core.Iec.Ladder;

/// <summary>
/// One network laid out: positioned primitives any renderer can draw without knowing what a
/// contact is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the layout emits primitives rather than "a contact at x".</b> Two renderers draw
/// this: the app's page, and later the Outpost React page. If each one drew its own contact
/// symbol, the two would drift, and a reviewer would see a different rung depending on where
/// they looked. Here every line, frame and label is already placed, so a renderer only
/// serialises, and the geometry tests check the drawing itself rather than a description of it.
/// </para>
/// <para>
/// Units are CSS pixels at 100% zoom. Text is drawn in a monospace font at
/// <see cref="LadderLayout.FontSize"/>, so <see cref="LadderLayout.CharWidth"/> is a measurement
/// the layout can trust: that is what lets it truncate a long tag name before the browser draws
/// it over its neighbour.
/// </para>
/// </remarks>
public sealed record LadderDrawing(
    double Width,
    double Height,
    IReadOnlyList<LdGroup> Groups,
    string? FallbackReason);

/// <summary>
/// The primitives of one element, or of the wiring between elements.
/// </summary>
/// <param name="Key">The element's node id, so a change mark can find it; empty for wiring.</param>
/// <param name="Kind">What is drawn: contact, coil, box, compare, jump, return, unsupported, wire, rail, placeholder.</param>
public sealed record LdGroup(string Key, string Kind, LdRect Bounds, IReadOnlyList<LdPrimitive> Items);

public readonly record struct LdRect(double X, double Y, double W, double H)
{
    public double Right => X + W;

    public double Bottom => Y + H;

    public bool Overlaps(LdRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public bool Contains(double x, double y) =>
        x >= X - 0.01 && x <= Right + 0.01 && y >= Y - 0.01 && y <= Bottom + 0.01;
}

public abstract record LdPrimitive;

public sealed record LdLine(double X1, double Y1, double X2, double Y2) : LdPrimitive;

/// <summary>A box outline.</summary>
public sealed record LdFrame(LdRect Rect, bool Dashed = false) : LdPrimitive;

/// <summary>A small circle on a box pin: the pin is negated.</summary>
public sealed record LdCircle(double X, double Y, double R) : LdPrimitive;

/// <summary>A curve, in SVG path syntax. Used for coil brackets only.</summary>
public sealed record LdPath(string Data) : LdPrimitive;

public enum LdAlign { Start, Middle, End }

/// <summary>
/// A label. <see cref="Text"/> is what fits; <see cref="Full"/> is what the vendor wrote, shown
/// on hover when the two differ.
/// </summary>
/// <param name="Y">The baseline.</param>
/// <param name="Role">operand, type, detail, pin, instance, symbol or placeholder: a CSS class, nothing more.</param>
public sealed record LdText(double X, double Y, string Text, string Full, LdAlign Align, string Role) : LdPrimitive
{
    /// <summary>The space the text takes, from the same monospace measure the layout used.</summary>
    public LdRect Bounds
    {
        get
        {
            var w = LadderLayout.TextWidth(Text);
            var x = Align switch
            {
                LdAlign.Start => X,
                LdAlign.Middle => X - (w / 2),
                _ => X - w,
            };
            return new LdRect(x, Y - LadderLayout.FontSize + 2, w, LadderLayout.FontSize);
        }
    }
}
