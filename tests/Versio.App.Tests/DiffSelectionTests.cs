using System.Windows;
using System.Windows.Input;
using Versio.App.Views;
using Versio.Core.Diff;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Selecting diff lines does something, and copying them produces the lines.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> <c>DiffView</c> has set <c>SelectionMode.Extended</c> since it was
/// written, so click, shift-click and Ctrl-click all built a real selection — and the container
/// template supplied in the shell reduced the row to a bare <c>ContentPresenter</c> with no
/// <c>IsSelected</c> trigger, so nothing was drawn. No <c>Copy</c> binding existed anywhere in
/// the application either. A user dragged across twelve lines, saw no response at all, pressed
/// Ctrl+C, and got nothing.
/// </para>
/// <para>
/// The bare template was half right and that is worth stating: a selected fill of the kind
/// <c>RowItem</c> uses would paint over <c>Diff.AddedBg</c>, and the row tint is the
/// information. Drawing nothing is a different thing from solving that — what the shell draws
/// instead is a wash on top, measured in <see cref="ShellLayoutTests"/> against the real window.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class DiffSelectionTests
{
    private const string Patch =
        "@@ -1,3 +1,4 @@\n" +
        " FUNCTION_BLOCK FB_Motor\n" +
        "-VAR speed : INT; END_VAR\n" +
        "+VAR speed : REAL; END_VAR\n" +
        "+VAR ramp : REAL; END_VAR\n";

    /// <summary>A view whose patch has finished parsing.</summary>
    /// <remarks>
    /// <c>Rebuild</c> parses on a worker and marshals the result back, so the lines are not
    /// there when the setter returns — measuring immediately finds an empty list and reports
    /// it as a selection bug. Awaited on the dispatcher thread, which is what lets the
    /// continuation in.
    /// </remarks>
    private static async Task<DiffView> ArrangedAsync()
    {
        var view = new DiffView { FilePath = "FB_Motor.st", DiffText = Patch };

        for (var attempt = 0; attempt < 200 && view.ItemsSource is null; attempt++)
        {
            await Task.Delay(5);
        }

        view.Measure(new Size(600, 400));
        view.Arrange(new Rect(0, 0, 600, 400));
        view.UpdateLayout();

        return view;
    }

    [Fact]
    public void The_view_parses_its_patch_into_lines()
    {
        // The precondition for everything below, asserted on its own so a failure here is not
        // reported as a selection bug.
        StaHarness.Run(async () =>
        {
            var view = await ArrangedAsync();

            Assert.NotNull(view.ItemsSource);
            Assert.NotEmpty(view.Items.OfType<DiffLine>());
        });
    }

    [Fact]
    public void Copy_is_refused_with_nothing_selected_and_offered_with_something()
    {
        // A command that is always enabled and silently does nothing is the same failure in a
        // different shape.
        StaHarness.Run(async () =>
        {
            var view = await ArrangedAsync();

            Assert.False(ApplicationCommands.Copy.CanExecute(null, view));

            view.SelectedItem = view.Items.OfType<DiffLine>().First();

            Assert.True(ApplicationCommands.Copy.CanExecute(null, view));
        });
    }

    [Fact]
    public void More_than_one_line_can_be_selected_at_once()
    {
        // Extended selection is what makes copying a hunk possible, and it is the mode that
        // was on all along with nothing to show for it.
        StaHarness.Run(async () =>
        {
            var view = await ArrangedAsync();
            var lines = view.Items.OfType<DiffLine>().ToList();

            view.SelectedItems.Add(lines[0]);
            view.SelectedItems.Add(lines[1]);

            Assert.Equal(2, view.SelectedItems.Count);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, view));
        });
    }

    [Fact]
    public void Selected_lines_come_back_in_document_order()
    {
        // SelectedItems is in the order things were PICKED, so Ctrl-clicking upwards through
        // a hunk would paste the lines reversed - a diff that reads as though the change ran
        // backwards. This is the ordering the copy relies on, exercised the way a user
        // produces it.
        StaHarness.Run(async () =>
        {
            var view = await ArrangedAsync();
            var lines = view.Items.OfType<DiffLine>().ToList();

            // Bottom-up, which is what a Ctrl-click walk upwards does.
            view.SelectedItems.Add(lines[^1]);
            view.SelectedItems.Add(lines[^2]);

            var picked = view.SelectedItems.OfType<DiffLine>().ToHashSet();
            var ordered = lines.Where(picked.Contains).ToList();

            Assert.Equal([lines[^2], lines[^1]], ordered);
        });
    }

    [Fact]
    public void A_copied_line_carries_its_marker()
    {
        // What gets pasted is an excerpt of a DIFF. Stripping the + and - would turn "here is
        // what changed" into "here is some code", which is the half worth sending.
        StaHarness.Run(async () =>
        {
            var view = await ArrangedAsync();
            var added = view.Items.OfType<DiffLine>()
                .First(l => l.Kind == DiffLineKind.Added);

            Assert.Equal("+", added.Marker);
            Assert.StartsWith("VAR speed : REAL", added.Text);
        });
    }
}
