using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The Rendered tab draws a file as itself: one gutter, no <c>@@</c> line, the
/// declaration and implementation behind a divider, numbered as the IDE numbers them.
/// </summary>
/// <remarks>
/// Which lines and which numbers is Core's (<c>StSourceLayoutTests</c>). What these prove
/// is the wiring that compiles clean when deleted: the Rendered template binding the rows
/// rather than the pseudo-diff, the row builder drawing them, and the copy pasting them.
/// </remarks>
[Collection("theme")]
public sealed class RenderedSourceTests
{
    private const string FbAlarm =
        "FUNCTION_BLOCK FB_Alarm\nVAR\n\tState : INT;\n\nEND_VAR\n\n"
        + StTextCodec.ImplementationDelimiter
        + "\n\n// Reset Latch\nIF NOT Latch THEN\n\tState := 0;\nEND_IF\n";

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
    public void The_rendered_template_draws_rows_not_the_pseudo_diff()
    {
        // Revert the template's binding to PseudoDiff and this is the test that notices: the
        // items become DiffLines again, @@ header and all.
        StaHarness.Run(async () =>
        {
            var logger = new VersioLogger(new SystemClock());
            var sink = new InMemoryLogSink();
            logger.AddSink(sink);
            using var shell = new ShellViewModel(logger, new LogViewModel(sink));
            var window = new MainWindow(shell);

            var rendered = Assert.Single(Logical(window).OfType<ContentControl>(),
                c => BindingOperations.GetBinding(c, ContentControl.ContentProperty)
                         ?.Path.Path == nameof(ShellViewModel.RenderedContent));

            var template = Assert.IsType<DataTemplate>(
                rendered.TryFindResource(new DataTemplateKey(typeof(SourceRender))));
            var view = Assert.IsAssignableFrom<DiffView>(template.LoadContent());

            var source = Source();
            view.DataContext = source;

            // A template's content outside a tree binds on the dispatcher's DataBind pass,
            // not on the assignment.
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Background);

            Assert.Same(source.Rows, view.ItemsSource);
            Assert.DoesNotContain(view.Items.OfType<object>(), i => i is DiffLine);
        });
    }

    [Fact]
    public void A_code_row_has_one_gutter_and_no_marker_column()
    {
        StaHarness.Run(() =>
        {
            var view = new Openable { FilePath = "FB_Alarm.st" };
            var row = Assert.IsType<Grid>(view.RowFor(new SourceCodeLine(2, "IF NOT Latch THEN", 12)));

            Assert.Equal(2, row.ColumnDefinitions.Count);
            var texts = row.Children.OfType<TextBlock>().Select(Text).ToList();

            // The section's number, not the file's: the IDE calls this line 2.
            Assert.Equal("2", texts[0]);
            Assert.Equal("IF NOT Latch THEN", texts[1]);
        });
    }

    [Fact]
    public void A_divider_is_drawn_with_its_label_and_a_rule()
    {
        StaHarness.Run(() =>
        {
            var view = new Openable { FilePath = "FB_Alarm.st" };
            var row = Assert.IsType<Grid>(view.RowFor(
                new SourceDivider(StSourceLayout.ImplementationLabel, StTextCodec.ImplementationDelimiter)));

            Assert.Contains(row.Children.OfType<TextBlock>(), t => t.Text == "IMPLEMENTATION");
            Assert.Contains(row.Children.OfType<Border>(), b => b.Height == 1);
        });
    }

    [Fact]
    public void Source_rows_win_over_diff_text_even_when_the_parse_lands_later()
    {
        // The generation guard is what stops a parse already in flight from replacing the
        // rows. Without it this view would end on DiffLines.
        StaHarness.Run(async () =>
        {
            var view = new DiffView
            {
                FilePath = "FB_Alarm.st",
                DiffText = "@@ -1,1 +1,1 @@\n x\n",
            };
            var rows = StSourceLayout.Lay(FbAlarm);
            view.SourceRows = rows;

            await Task.Delay(100);

            Assert.Same(rows, view.ItemsSource);
        });
    }

    [Fact]
    public void Copying_across_the_divider_pastes_the_file()
    {
        // No markers, because nothing is being compared; the divider as the delimiter line it
        // stands for, so the excerpt is still valid .st; the Declaration label as nothing.
        StaHarness.Run(() =>
        {
            var rows = StSourceLayout.Lay(FbAlarm);
            var view = new DiffView { FilePath = "FB_Alarm.st", SourceRows = rows };

            foreach (var row in rows)
            {
                view.SelectedItems.Add(row);
            }

            var lines = view.SelectionText()!.Split(Environment.NewLine);

            Assert.Equal("FUNCTION_BLOCK FB_Alarm", lines[0]);
            Assert.Contains(StTextCodec.ImplementationDelimiter, lines);
            Assert.DoesNotContain(StSourceLayout.DeclarationLabel, lines);
            Assert.Equal("END_IF", lines[^1]);
        });
    }

    [Fact]
    public void Selecting_one_blank_line_selects_only_that_one()
    {
        // Line 4 is blank in the declaration; the implementation has none, so add one that
        // shares its section number. Equal records would both be selected by one click.
        StaHarness.Run(() =>
        {
            var rows = StSourceLayout.Lay(
                StTextCodec.Join("A\nB\nC\n\nD", "1\n2\n3\n\n5"));
            var view = new DiffView { FilePath = "x.st", SourceRows = rows };

            var blanks = rows.OfType<SourceCodeLine>().Where(l => l.Number == 4).ToList();
            Assert.Equal(2, blanks.Count);

            view.SelectedItems.Add(blanks[0]);

            Assert.Single(view.SelectedItems);
            Assert.Equal(string.Empty, view.SelectionText());
        });
    }

    [Fact]
    public void Side_by_side_draws_a_marked_source_in_two_columns_and_an_unmarked_one_in_one()
    {
        // The owner, 2026-10-10: "side by side not working on st". The box changed Raw and the
        // ladder, and the Rendered source drew one column whatever it said.
        StaHarness.Run(() =>
        {
            var before = FbAlarm.Replace("State := 0;", "State := 1;", StringComparison.Ordinal);
            var marked = StSourceLayout.Mark(StSourceLayout.Lay(FbAlarm), before, FbAlarm);

            var split = new Openable { FilePath = "FB_Alarm.st", SideBySide = true, SourceRows = marked };
            var pairs = Assert.IsAssignableFrom<IReadOnlyList<SourceSplitRow>>(split.ItemsSource);
            var changed = Assert.Single(pairs, p => p.Left?.Kind == SourceSide.Removed);
            Assert.Equal(("\tState := 1;", "\tState := 0;"), (changed.Left!.Text, changed.Right!.Text));
            var row = Assert.IsType<Grid>(split.RowFor(changed));
            Assert.Equal(3, row.ColumnDefinitions.Count);

            // Unified draws the working file: the removed line is the split view's alone.
            var unified = new Openable { FilePath = "FB_Alarm.st", SideBySide = false, SourceRows = marked };
            var rows = Assert.IsAssignableFrom<IReadOnlyList<SourceRow>>(unified.ItemsSource);
            Assert.DoesNotContain(rows, r => r is SourceRemovedLine);
            Assert.Equal(marked.Count(r => r is not SourceRemovedLine), rows.Count);

            // Nothing marked: the same text twice would be noise, so one column even when ticked.
            var clean = StSourceLayout.Lay(FbAlarm);
            Assert.Same(clean, new Openable { FilePath = "FB_Alarm.st", SideBySide = true, SourceRows = clean }.ItemsSource);
        });
    }

    [Fact]
    public void The_rendered_template_follows_the_panes_side_by_side_box()
    {
        StaHarness.Run(() =>
        {
            var logger = new VersioLogger(new SystemClock());
            var sink = new InMemoryLogSink();
            logger.AddSink(sink);
            using var shell = new ShellViewModel(logger, new LogViewModel(sink));
            var window = new MainWindow(shell);

            var rendered = Assert.Single(Logical(window).OfType<ContentControl>(),
                c => BindingOperations.GetBinding(c, ContentControl.ContentProperty)
                         ?.Path.Path == nameof(ShellViewModel.RenderedContent));
            var template = Assert.IsType<DataTemplate>(
                rendered.TryFindResource(new DataTemplateKey(typeof(SourceRender))));
            var view = Assert.IsAssignableFrom<DiffView>(template.LoadContent());

            Assert.Equal("DataContext.DiffSideBySide", BindingOperations.GetBinding(view, DiffView.SideBySideProperty)?.Path.Path);

            // And its Show full file box: unbound, the view draws everything and the box does
            // nothing on this tab, which is the report this binding answers.
            Assert.Equal("DataContext.ShowFullFile", BindingOperations.GetBinding(view, DiffView.ShowFullFileProperty)?.Path.Path);
        });
    }

    [Fact]
    public void Unified_marks_where_removed_lines_stood_without_drawing_them()
    {
        // The owner, 2026-10-22: deleted lines belong in side by side only. A change that only
        // deleted must still show somewhere on this tab, so the line after it carries the rule.
        StaHarness.Run(() =>
        {
            var before = FbAlarm.Replace("\tState := 0;\n", "\tState := 0;\n\tLatch := FALSE;\n", StringComparison.Ordinal);
            var marked = StSourceLayout.Mark(StSourceLayout.Lay(FbAlarm), before, FbAlarm);
            Assert.Single(marked.OfType<SourceRemovedLine>());

            var view = new Openable { FilePath = "FB_Alarm.st", SourceRows = marked };
            var rows = Assert.IsAssignableFrom<IReadOnlyList<SourceRow>>(view.ItemsSource);
            Assert.DoesNotContain(rows, r => r is SourceRemovedLine);

            var after = Assert.Single(rows.OfType<SourceCodeLine>(), l => l.Text == "END_IF");
            var row = Assert.IsType<Grid>(view.RowFor(after));
            Assert.Contains(row.Children.OfType<System.Windows.Shapes.Rectangle>(), r => r.VerticalAlignment == VerticalAlignment.Top);
            Assert.Contains("1 line removed above", row.ToolTip as string, StringComparison.Ordinal);

            var untouched = Assert.IsType<Grid>(view.RowFor(Assert.Single(rows.OfType<SourceCodeLine>(), l => l.Text == "\tState := 0;")));
            Assert.Empty(untouched.Children.OfType<System.Windows.Shapes.Rectangle>());
        });
    }

    [Fact]
    public void Show_full_file_cleared_folds_the_unchanged_runs_in_both_modes()
    {
        // The box re-read git for Raw and the Rendered tab drew the whole file whatever it said
        // (the owner, 2026-10-22: "show full file does nothing").
        StaHarness.Run(() =>
        {
            var body = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"x{i} := {i};"));
            var current = StTextCodec.Join("PROGRAM P\nVAR\nEND_VAR", body);
            var before = current.Replace("x15 := 15;", "x15 := 0;", StringComparison.Ordinal);
            var marked = StSourceLayout.Mark(StSourceLayout.Lay(current), before, current);

            var whole = new Openable { FilePath = "P.st", SourceRows = marked, ShowFullFile = true };
            Assert.DoesNotContain(Assert.IsAssignableFrom<IReadOnlyList<SourceRow>>(whole.ItemsSource), r => r is SourceFold);

            var folded = new Openable { FilePath = "P.st", SourceRows = marked, ShowFullFile = false };
            var rows = Assert.IsAssignableFrom<IReadOnlyList<SourceRow>>(folded.ItemsSource);
            // The declaration's three, x1..x11 (x12..x14 are context), and x19..x20. Dividers stay.
            Assert.Equal([3, 11, 2], rows.OfType<SourceFold>().Select(f => f.Hidden));
            Assert.Equal(2, rows.Count(r => r is SourceDivider));
            var fold = rows.OfType<SourceFold>().Single(f => f.Hidden == 11);
            Assert.Contains("11 unchanged lines", string.Concat(Logical(folded.RowFor(fold)).OfType<TextBlock>().Select(Text)), StringComparison.Ordinal);

            var split = new Openable { FilePath = "P.st", SourceRows = marked, SideBySide = true, ShowFullFile = false };
            var pairs = Assert.IsAssignableFrom<IReadOnlyList<SourceSplitRow>>(split.ItemsSource);
            Assert.Equal([3, 11, 2], pairs.Where(p => p.Fold is not null).Select(p => p.Fold!.Hidden));

            folded.ShowFullFile = true;
            Assert.DoesNotContain(Assert.IsAssignableFrom<IReadOnlyList<SourceRow>>(folded.ItemsSource), r => r is SourceFold);
        });
    }

    private static SourceRender Source() =>
        new() { Path = "FB_Alarm.st", PseudoDiff = "@@ -1,1 +1,1 @@\n x\n", Rows = StSourceLayout.Lay(FbAlarm) };

    private static string Text(TextBlock block) =>
        block.Inlines.Count == 0
            ? block.Text
            : string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in Logical(child))
            {
                yield return descendant;
            }
        }
    }
}
