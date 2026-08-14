using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Every keyed style in the stylesheet resolves its own references and renders.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this pins.</b> The stylesheet is six dictionaries now rather than one file, and a
/// <c>StaticResource</c> in a merged dictionary resolves against itself and its own merges
/// only — never against a sibling the parent merged alongside it. <c>Controls.xaml</c>'s header
/// records what that costs: a <c>XamlParseException</c> reading
/// <c>Cannot find resource named 'Motion.Fast'</c>, thrown the first time something rendered a
/// rail button, from a file that had compiled with zero warnings.
/// </para>
/// <para>
/// So the split that made <c>Buttons</c>, <c>Inputs</c>, <c>Rows</c>, <c>ScrollBars</c> and
/// <c>Chrome</c> separate files put every one of them one missing merge away from that same
/// throw — and the build cannot see it. <c>FocusRingInset</c> and <c>PaneSplitter</c> were the
/// sharp cases: both reach across a dictionary boundary now, and neither had a test naming it.
/// </para>
/// <para>
/// <b>Rendered, not looked up.</b> Asking whether a key exists proves nothing here; the key
/// resolves fine and the template behind it throws when something instantiates it. So every
/// <see cref="ControlTemplate"/> a style carries is loaded, and every style with a concrete
/// target is applied to a real control and put through a measure and arrange pass.
/// </para>
/// <para>
/// This is deliberately a sweep rather than a list. A style added to any of those files is
/// covered the day it is added, without anyone remembering to come here — which is the
/// property the four controls in <c>Controls.xaml</c>'s header lost by being styled late.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class StyleSheetResolutionTests
{
    [Fact]
    public void Every_keyed_style_resolves_its_references_and_renders()
    {
        StaHarness.Run(() =>
        {
            var styles = KeyedStyles();

            // A sweep that found nothing would pass in silence, which is the one way this
            // test could rot without failing. The stylesheet held 24 keyed styles when this
            // was written; the floor is well under that so adding files never trips it.
            Assert.True(styles.Count >= 20,
                        $"only {styles.Count} keyed styles were found in the application " +
                        "dictionary, so the stylesheet is not being merged and this test is " +
                        "checking nothing");

            // Styles that threw while being realised out of the dictionary never became a
            // Style at all, so they arrive already failed.
            var failures = new List<string>(Unrealisable);

            foreach (var (key, style) in styles)
            {
                try
                {
                    Render(style);
                }
                catch (Exception ex)
                {
                    // Collected rather than thrown, so one broken merge reports every style
                    // it broke instead of only the first one alphabetically.
                    failures.Add($"  {key} ({style.TargetType?.Name ?? "untyped"}): " +
                                 $"{Innermost(ex)}");
                }
            }

            Assert.True(failures.Count == 0,
                        $"{failures.Count} of {styles.Count} styles failed to render — a " +
                        "dictionary is missing a merge it needs, or a resource moved out " +
                        "from under it:\n" + string.Join("\n", failures));
        });
    }

    /// <summary>
    /// Load every template the style carries, then apply it to a real control and lay it out.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they catch different things. <see cref="FrameworkTemplate.LoadContent"/>
    /// instantiates the template's visual tree, which is what forces a <c>StaticResource</c>
    /// inside it to resolve — and it reaches the untyped focus-ring styles, which have no
    /// target to apply them to. The arrange pass then covers what a style does through its
    /// setters and triggers rather than its template.
    /// </remarks>
    private static void Render(Style style)
    {
        foreach (var template in Templates(style))
        {
            template.LoadContent();
        }

        var target = style.TargetType;
        if (target is null || target.IsAbstract || !typeof(FrameworkElement).IsAssignableFrom(target))
        {
            // ButtonBase and the untyped focus rings land here. Their templates were loaded
            // above, which is the part that resolves resources.
            return;
        }

        if (Activator.CreateInstance(target) is not FrameworkElement element)
        {
            return;
        }

        element.Style = style;

        var host = new Grid { Width = 240, Height = 48 };
        host.Children.Add(element);
        host.Measure(new Size(240, 48));
        host.Arrange(new Rect(0, 0, 240, 48));
        host.UpdateLayout();
    }

    /// <summary>Every control template reachable from a style's setters or its triggers.</summary>
    private static IEnumerable<ControlTemplate> Templates(Style style)
    {
        foreach (var setter in style.Setters.OfType<Setter>())
        {
            if (setter.Value is ControlTemplate template)
            {
                yield return template;
            }
        }

        foreach (var trigger in style.Triggers)
        {
            // Setters is declared on each concrete trigger rather than on TriggerBase, so
            // there is no one property to read them all through.
            var setters = trigger switch
            {
                Trigger t => t.Setters,
                MultiTrigger t => t.Setters,
                DataTrigger t => t.Setters,
                MultiDataTrigger t => t.Setters,
                _ => null,
            };

            foreach (var setter in (setters ?? []).OfType<Setter>())
            {
                if (setter.Value is ControlTemplate template)
                {
                    yield return template;
                }
            }
        }
    }

    /// <summary>
    /// Keys that threw on the way out of the dictionary, described the way a failure is.
    /// </summary>
    /// <remarks>
    /// A keyed style is stored deferred, so a <c>StaticResource</c> it cannot resolve throws
    /// from the INDEXER rather than from anything this test does with the style afterwards.
    /// Left uncaught that ends the sweep at the first broken key with a stack trace pointing
    /// at the collector; caught here, every broken key is named and the run continues.
    /// </remarks>
    private static readonly List<string> Unrealisable = [];

    /// <summary>Every string-keyed <see cref="Style"/> in the merged application dictionary.</summary>
    private static List<(string Key, Style Style)> KeyedStyles()
    {
        var found = new List<(string, Style)>();
        Unrealisable.Clear();
        Collect(Application.Current.Resources, found, new HashSet<ResourceDictionary>());
        return found;
    }

    /// <summary>The message that actually names the problem, not the wrapper around it.</summary>
    /// <remarks>
    /// A resource that will not resolve surfaces as a <c>XamlParseException</c> whose own
    /// message says only that something threw; the sentence naming the missing key is on the
    /// inner exception.
    /// </remarks>
    private static string Innermost(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        return inner.Message;
    }

    private static void Collect(ResourceDictionary dictionary,
                                List<(string, Style)> into,
                                HashSet<ResourceDictionary> seen)
    {
        // Primitives.xaml is merged by every leaf as well as by Controls.xaml, so the same
        // dictionary is reached several times over. Without this the focus rings would be
        // rendered six times and reported six times when they broke.
        if (!seen.Add(dictionary))
        {
            return;
        }

        foreach (var key in dictionary.Keys)
        {
            if (key is not string name)
            {
                continue;
            }

            // Indexed rather than enumerated as entries: a keyed style is stored deferred,
            // and only the indexer realises it — which is also where an unresolvable
            // StaticResource inside it throws.
            try
            {
                if (dictionary[key] is Style style)
                {
                    into.Add((name, style));
                }
            }
            catch (Exception ex)
            {
                Unrealisable.Add($"  {name}: {Innermost(ex)}");
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            Collect(merged, into, seen);
        }
    }
}
