using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Versio.Core.Text;

namespace Versio.App.Views;

/// <summary>
/// A parsed Markdown document drawn as a page: the Rendered tab for <c>.md</c> files.
/// </summary>
/// <remarks>
/// <para>
/// <b>A FlowDocument, not the DiffView.</b> Every other rendered view reuses the code-drawing
/// control, and that is exactly why Markdown looked unrendered: a document is prose that wraps
/// to the pane, with tables and lists, and a gutter-numbered line list is the wrong shape for
/// it however it is coloured. FlowDocument wraps, selects and copies for free.
/// </para>
/// <para>
/// <b>Every colour and size is a resource REFERENCE</b> (<c>SetResourceReference</c>), never a
/// looked-up value: a document built in the dark theme has to follow a switch to light without
/// being rebuilt, the same rule the XAML follows with DynamicResource.
/// </para>
/// <para>
/// Links run through <see cref="LinkCommand"/> with the target as the parameter, so following
/// one is the view model's decision and lands in the log; this control never opens anything.
/// </para>
/// </remarks>
public sealed class MarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty BlocksProperty =
        DependencyProperty.Register(nameof(Blocks), typeof(IReadOnlyList<MdBlock>), typeof(MarkdownView),
            new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Rebuild()));

    public static readonly DependencyProperty LinkCommandProperty =
        DependencyProperty.Register(nameof(LinkCommand), typeof(ICommand), typeof(MarkdownView),
            new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Rebuild()));

    public MarkdownView()
    {
        IsToolBarVisible = false;
        Zoom = 100;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = true;
    }

    public IReadOnlyList<MdBlock>? Blocks
    {
        get => (IReadOnlyList<MdBlock>?)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public ICommand? LinkCommand
    {
        get => (ICommand?)GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    private void Rebuild()
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(18, 10, 18, 24),

            // A reading measure. Prose stretched across a 2,000 px pane is a line the eye
            // cannot find the start of again; tables and code still get the full measure.
            MaxPageWidth = 980,
            TextAlignment = TextAlignment.Left,
            IsHyphenationEnabled = false,
        };
        document.SetResourceReference(FlowDocument.FontFamilyProperty, "Font.UI");
        document.SetResourceReference(FlowDocument.FontSizeProperty, "Font.Size.Body");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.Text.Primary");
        document.Background = Brushes.Transparent;

        if (Blocks is { } blocks)
        {
            foreach (var block in blocks)
            {
                document.Blocks.Add(Build(block, nested: false));
            }
        }

        Document = document;
    }

    private Block Build(MdBlock block, bool nested) => block switch
    {
        MdHeading h => Heading(h),
        MdParagraph p => Para(p.Runs, new Thickness(0, 0, 0, nested ? 4 : 10)),
        MdCode c => Code(c),
        MdQuote q => Quote(q),
        MdList l => ListOf(l, nested),
        MdTable t => TableOf(t),
        MdRule => Rule(),
        _ => new Paragraph(),
    };

    private Paragraph Heading(MdHeading heading)
    {
        var paragraph = Para(heading.Runs, new Thickness(0, heading.Level <= 2 ? 18 : 14, 0, 8));
        paragraph.SetResourceReference(TextElement.FontWeightProperty, "Font.Weight.Semibold");
        paragraph.SetResourceReference(TextElement.FontSizeProperty, heading.Level switch
        {
            1 => "Font.Size.Title",
            2 => "Font.Size.Subtitle",
            _ => "Font.Size.Body",
        });

        // The two levels that divide a document get the rule under them that every Markdown
        // reader draws; without it an H2 in this font is only slightly larger body text.
        if (heading.Level <= 2)
        {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 4);
            paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.Border.Subtle");
        }

        if (heading.Level >= 4)
        {
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Secondary");
        }

        return paragraph;
    }

    private Paragraph Para(IReadOnlyList<MdRun> runs, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        AddRuns(paragraph.Inlines, runs);
        return paragraph;
    }

    private void AddRuns(InlineCollection inlines, IReadOnlyList<MdRun> runs)
    {
        Hyperlink? link = null;

        foreach (var run in runs)
        {
            if (run.Link is null)
            {
                link = null;
                AddText(inlines, run);
                continue;
            }

            // Consecutive runs with one target are one link: "[**bold** text](x)" is a single
            // thing to press, not two.
            if (link is null || !Equals(link.CommandParameter, run.Link))
            {
                link = new Hyperlink
                {
                    Command = LinkCommand,
                    CommandParameter = run.Link,
                    ToolTip = run.Link,
                    Cursor = Cursors.Hand,
                };
                link.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent.Default");
                inlines.Add(link);
            }

            AddText(link.Inlines, run);
        }
    }

    private static void AddText(InlineCollection inlines, MdRun run)
    {
        // A hard break inside a paragraph arrives as "\n" in the run's text.
        var parts = run.Text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                inlines.Add(new LineBreak());
            }

            if (parts[i].Length > 0)
            {
                inlines.Add(Styled(parts[i], run.Style));
            }
        }
    }

    private static Run Styled(string text, MdStyle style)
    {
        var run = new Run(text);

        if (style.HasFlag(MdStyle.Strong))
        {
            run.SetResourceReference(TextElement.FontWeightProperty, "Font.Weight.Semibold");
        }

        if (style.HasFlag(MdStyle.Emphasis))
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (style.HasFlag(MdStyle.Strike))
        {
            // Struck-through text in this repository is a closed loose end: kept for the
            // record, read past. Dimmed as well as struck, so it recedes.
            run.TextDecorations = TextDecorations.Strikethrough;
            run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Secondary");
        }

        if (style.HasFlag(MdStyle.Code))
        {
            run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
            run.SetResourceReference(TextElement.FontSizeProperty, "Font.Size.Code");
            run.SetResourceReference(TextElement.BackgroundProperty, "Brush.Surface.Raised");
        }

        return run;
    }

    private static Paragraph Code(MdCode code)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(1),
        };
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
        paragraph.SetResourceReference(TextElement.FontSizeProperty, "Font.Size.Code");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "Brush.Surface.Sunken");
        paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.Border.Subtle");

        var lines = code.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[i]));
        }

        return paragraph;
    }

    private Section Quote(MdQuote quote)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12, 2, 0, 2),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
        section.SetResourceReference(Block.BorderBrushProperty, "Brush.Border.Strong");
        section.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Secondary");

        foreach (var block in quote.Blocks)
        {
            section.Blocks.Add(Build(block, nested: true));
        }

        return section;
    }

    private List ListOf(MdList list, bool nested)
    {
        var result = new List
        {
            MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = Math.Max(1, list.Start),
            Margin = new Thickness(0, 0, 0, nested ? 2 : 10),
            Padding = new Thickness(22, 0, 0, 0),
        };

        foreach (var item in list.Items)
        {
            var listItem = new ListItem();
            foreach (var block in item)
            {
                listItem.Blocks.Add(Build(block, nested: true));
            }

            // An empty item still needs a block, or the marker has nothing to sit beside.
            if (listItem.Blocks.Count == 0)
            {
                listItem.Blocks.Add(new Paragraph());
            }

            result.ListItems.Add(listItem);
        }

        return result;
    }

    private Table TableOf(MdTable table)
    {
        var result = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1, 1, 0, 0),
        };
        result.SetResourceReference(Block.BorderBrushProperty, "Brush.Border.Subtle");

        // FlowDocument tables have no auto width: every column is an equal share unless told
        // otherwise, which crushes a key column beside its description. Weighted by the longest
        // text each column holds, clamped so a one-letter column stays legible and a paragraph
        // column cannot take everything.
        for (var c = 0; c < table.Header.Count; c++)
        {
            var longest = table.Rows
                .Select(r => c < r.Count ? Length(r[c]) : 0)
                .Append(Length(table.Header[c]))
                .Max();
            result.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(longest, 6, 60), GridUnitType.Star) });
        }

        var header = new TableRowGroup();
        header.SetResourceReference(TextElement.BackgroundProperty, "Brush.Surface.Raised");
        header.SetResourceReference(TextElement.FontWeightProperty, "Font.Weight.Semibold");

        // A table whose header is all empty cells - "| | |", a key/value table - has no
        // header to draw, and an empty raised strip on top of it reads as a rendering fault.
        if (table.Header.Any(h => Length(h) > 0))
        {
            header.Rows.Add(Row(table.Header, table.Align));
            result.RowGroups.Add(header);
        }

        var body = new TableRowGroup();
        foreach (var row in table.Rows)
        {
            body.Rows.Add(Row(row, table.Align));
        }

        result.RowGroups.Add(body);
        return result;
    }

    private TableRow Row(IReadOnlyList<IReadOnlyList<MdRun>> cells, IReadOnlyList<MdAlign> align)
    {
        var row = new TableRow();
        for (var c = 0; c < cells.Count; c++)
        {
            var paragraph = Para(cells[c], new Thickness(0));
            paragraph.TextAlignment = (c < align.Count ? align[c] : MdAlign.None) switch
            {
                MdAlign.Center => TextAlignment.Center,
                MdAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };

            var cell = new TableCell(paragraph)
            {
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0, 0, 1, 1),
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "Brush.Border.Subtle");
            row.Cells.Add(cell);
        }

        return row;
    }

    private static Paragraph Rule()
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 16),
            BorderThickness = new Thickness(0, 0, 0, 1),
            FontSize = 1,
            LineHeight = 1,
        };
        paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.Border.Subtle");
        return paragraph;
    }

    private static int Length(IReadOnlyList<MdRun> runs) => runs.Sum(r => r.Text.Length);
}
