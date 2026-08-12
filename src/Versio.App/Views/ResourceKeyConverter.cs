using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Versio.App.Views;

/// <summary>
/// Resolves a theme resource key produced by a view model into the resource itself.
/// </summary>
/// <remarks>
/// <para>
/// View models decide <i>which</i> semantic colour a row deserves — "this project failed",
/// "this file was deleted" — and say so with a key such as <c>Brush.Status.Deleted</c>. They
/// must not hand out a <c>Brush</c>: that would put a WPF type and the current theme's
/// palette into logic that is meant to be about sync state, and it would freeze the colour
/// at the moment the row was built, so a light/dark switch would leave stale brushes behind.
/// </para>
/// <para>
/// Looked up through <see cref="FrameworkElement.TryFindResource"/> on the application so the
/// active theme dictionary wins, and re-resolved on every binding evaluation.
/// </para>
/// </remarks>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
        {
            return DependencyProperty.UnsetValue;
        }

        // UnsetValue rather than null on a miss: it lets the binding fall back to whatever
        // the style or the inherited value says, instead of painting the row transparent.
        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Resource keys are resolved one way only.");
}
