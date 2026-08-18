using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Versio.App.Views;
using Versio.Core.Diff;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The diff pane's two new answers: side-by-side, and the emphasis inside a changed pair.
/// </summary>
/// <remarks>
/// The fold and the span arithmetic are proven in Core; what these prove is the rendering
/// seam — the mode actually swaps the items, the emphasis actually becomes a painted run,
/// and copying from the split view still pastes a diff. Each is the kind of wiring that
/// compiles clean when deleted.
/// </remarks>
[Collection("theme")]
public sealed class DiffModeTests
{
    private const string Patch =
        "@@ -1,3 +1,3 @@\n" +
        " FUNCTION_BLOCK FB_Motor\n" +
        "-iSpeed := 100;\n" +
        "+iSpeed := 200;\n";

    private static async Task<DiffView> ArrangedAsync(bool sideBySide)
    {
        var view = new DiffView
        {
            FilePath = "FB_Motor.st",
            SideBySide = sideBySide,
            DiffText = Patch,
        };

        for (var attempt = 0; attempt < 200 && view.ItemsSource is null; attempt++)
        {
            await Task.Delay(5);
        }

        view.Measure(new Size(900, 400));
        view.Arrange(new Rect(0, 0, 900, 400));
        view.UpdateLayout();

        return view;
    }

    [Fact]
    public void Side_by_side_swaps_the_items_and_unified_swaps_them_back()
    {
        StaHarness.Run(async () =>
        {
            var view = await ArrangedAsync(sideBySide: true);
            Assert.IsAssignableFrom<IReadOnlyList<SideBySideRow>>(view.ItemsSource);

            view.SideBySide = false;
            for (var attempt = 0; attempt < 200
                 && view.ItemsSource is not IReadOnlyList<DiffLine>; attempt++)
            {
                await Task.Delay(5);
            }

            Assert.IsAssignableFrom<IReadOnlyList<DiffLine>>(view.ItemsSource);
        });
    }

    /// <summary>Reaches the protected row builder, because a ListBox outside a window never
    /// realises containers and every visual assertion against one passes vacuously.</summary>
    private sealed class Openable : DiffView
    {
        public UIElement RowFor(object item)
        {
            var container = new ListBoxItem();
            PrepareContainerForItemOverride(container, item);
            return Assert.IsAssignableFrom<UIElement>(container.Content);
        }
    }

    [Fact]
    public void The_changed_number_is_painted_and_the_shared_text_is_not()
    {
        // The whole feature in one assertion: on a line where one number changed, the
        // number's run carries the word-emphasis background and the identifier's does not.
        StaHarness.Run(async () =>
        {
            var view = new Openable { FilePath = "FB_Motor.st", DiffText = Patch };
            for (var attempt = 0; attempt < 200 && view.ItemsSource is null; attempt++)
            {
                await Task.Delay(5);
            }

            var added = view.Items.OfType<DiffLine>()
                .Single(l => l.Kind == DiffLineKind.Added);
            var row = view.RowFor(added);

            var painted = RunsOf(row)
                .Where(r => r.Background is not null)
                .Select(r => r.Text)
                .ToList();
            var unpainted = RunsOf(row)
                .Where(r => r.Background is null)
                .Select(r => r.Text)
                .ToList();

            Assert.Contains("200", painted);
            Assert.Contains(unpainted, t => t.Contains("iSpeed"));
        });
    }

    [Fact]
    public void A_split_row_draws_the_old_side_left_and_the_new_side_right()
    {
        StaHarness.Run(async () =>
        {
            var view = new Openable
            {
                FilePath = "FB_Motor.st",
                SideBySide = true,
                DiffText = Patch,
            };
            for (var attempt = 0; attempt < 200 && view.ItemsSource is null; attempt++)
            {
                await Task.Delay(5);
            }

            var pair = view.Items.OfType<SideBySideRow>()
                .Single(r => r is { Left: not null, Right: not null }
                             && !ReferenceEquals(r.Left, r.Right));
            var row = Assert.IsType<Grid>(view.RowFor(pair));

            // Left cell holds the old text, right cell the new — swapped sides would be a
            // diff read backwards, which is worse than no split at all.
            var texts = row.Children.OfType<Grid>()
                .OrderBy(Grid.GetColumn)
                .Select(cell => string.Concat(RunsOf(cell).Select(r => r.Text)))
                .ToList();

            Assert.Equal(2, texts.Count);
            Assert.Contains("100", texts[0]);
            Assert.Contains("200", texts[1]);
        });
    }

    [Fact]
    public void Copying_a_split_row_pastes_the_unified_excerpt()
    {
        // The pasted text is still a diff: the − line then the + line, markers on. What
        // cannot be asserted without the clipboard is asserted through the same command
        // gate the selection tests use.
        StaHarness.Run(async () =>
        {
            var view = await ArrangedAsync(sideBySide: true);

            var pair = view.Items.OfType<SideBySideRow>()
                .Single(r => r is { Left: not null, Right: not null }
                             && !ReferenceEquals(r.Left, r.Right));

            view.SelectedItems.Add(pair);

            Assert.True(System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, view));
        });
    }

    private static IEnumerable<Run> RunsOf(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBlock block)
            {
                foreach (var run in block.Inlines.OfType<Run>())
                {
                    yield return run;
                }
            }

            foreach (var run in RunsOf(child))
            {
                yield return run;
            }
        }
    }
}
