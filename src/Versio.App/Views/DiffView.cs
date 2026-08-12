using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using Versio.Core.Diff;

namespace Versio.App.Views;

/// <summary>
/// Renders a unified diff with a line-number gutter and per-language syntax highlighting.
/// </summary>
/// <remarks>
/// <para>
/// Built on a virtualized <see cref="ListBox"/> rather than a rich text control. A 2.5 MB
/// visualisation diff is a realistic input here - the fixture project contains one, 33,644
/// lines long - and a <c>FlowDocument</c> would try to lay all of it out at once.
/// </para>
/// <para>
/// <b>The items are diff LINES, not built rows.</b> This class previously handed the list a
/// pre-built <c>UIElement</c> per line, which made the virtualization decorative: the panel
/// dutifully realised only the visible containers, but every Grid, gutter and syntax-tokenized
/// TextBlock had already been constructed before the list ever saw them. Selecting that one
/// visualisation file locked the UI for as long as it took to build thirty-three thousand
/// WPF elements. Rows are now built in <see cref="PrepareContainerForItemOverride"/>, so the
/// cost is paid only for lines somebody can actually see.
/// </para>
/// <para>
/// Parsing runs off the UI thread. It is fast, but "fast" on a 2.5 MB input is still long
/// enough to drop frames, and the work has no reason to be on the dispatcher.
/// </para>
/// </remarks>
public sealed class DiffView : ListBox
{
    public static readonly DependencyProperty DiffTextProperty =
        DependencyProperty.Register(nameof(DiffText), typeof(string), typeof(DiffView),
            new PropertyMetadata(string.Empty, OnInputChanged));

    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(DiffView),
            new PropertyMetadata(string.Empty, OnInputChanged));

    /// <summary>Invoked with a hunk index when its button is pressed.</summary>
    public static readonly DependencyProperty HunkCommandProperty =
        DependencyProperty.Register(nameof(HunkCommand), typeof(ICommand), typeof(DiffView),
            new PropertyMetadata(null));

    /// <summary>Whether a hunk button appears at all.</summary>
    public static readonly DependencyProperty ShowHunkActionsProperty =
        DependencyProperty.Register(nameof(ShowHunkActions), typeof(bool), typeof(DiffView),
            new PropertyMetadata(false, OnInputChanged));

    /// <summary>What the button says, and therefore what it does.</summary>
    public static readonly DependencyProperty HunkActionLabelProperty =
        DependencyProperty.Register(nameof(HunkActionLabel), typeof(string), typeof(DiffView),
            new PropertyMetadata("Stage hunk", OnInputChanged));

    public string DiffText
    {
        get => (string)GetValue(DiffTextProperty);
        set => SetValue(DiffTextProperty, value);
    }

    public string FilePath
    {
        get => (string)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public ICommand? HunkCommand
    {
        get => (ICommand?)GetValue(HunkCommandProperty);
        set => SetValue(HunkCommandProperty, value);
    }

    public bool ShowHunkActions
    {
        get => (bool)GetValue(ShowHunkActionsProperty);
        set => SetValue(ShowHunkActionsProperty, value);
    }

    public string HunkActionLabel
    {
        get => (string)GetValue(HunkActionLabelProperty);
        set => SetValue(HunkActionLabelProperty, value);
    }

    public DiffView()
    {
        ItemTemplate = null;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        VirtualizingStackPanel.SetIsVirtualizing(this, true);
        VirtualizingStackPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        SelectionMode = SelectionMode.Extended;
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DiffView)d).Rebuild();

    // Set before ItemsSource, so a container can never be prepared without one.
    private SyntaxLanguage _language = LanguageCatalog.ForPath(null);

    /// <summary>Guards against a slow parse landing after a newer selection.</summary>
    private int _generation;

    private async void Rebuild()
    {
        var text = DiffText;
        var path = FilePath;
        var generation = ++_generation;

        _language = LanguageCatalog.ForPath(path);

        if (string.IsNullOrEmpty(text))
        {
            ItemsSource = null;
            return;
        }

        // Off the dispatcher. Parsing is linear and quick per line, but a 2.5 MB diff is
        // still tens of thousands of them, and none of that work needs the UI thread.
        var document = await Task.Run(() => UnifiedDiffParser.Parse(text)).ConfigureAwait(true);

        // A user clicking down a list faster than the parses complete would otherwise see
        // an earlier, slower file win and replace the one they are looking at.
        if (generation != _generation)
        {
            return;
        }

        ItemsSource = document.IsEmpty ? null : document.Lines;
    }

    /// <summary>
    /// Build a row when its container is realised, and not before.
    /// </summary>
    /// <remarks>
    /// This is what makes the virtualization real. With recycling enabled the same container
    /// is handed a different line as the user scrolls, so this runs again for the new content
    /// - which is exactly the behaviour wanted, and the reason the row is rebuilt rather than
    /// mutated.
    /// </remarks>
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);

        if (element is ContentControl container && item is DiffLine line)
        {
            container.Content = BuildRow(line, _language);
        }
    }

    private UIElement BuildRow(DiffLine line, SyntaxLanguage language)
    {
        var grid = new Grid { Background = BackgroundFor(line) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Gutter(line.OldLine, 0));
        grid.Children.Add(Gutter(line.NewLine, 1));

        var marker = Mono(line.Marker, MarkerBrush(line));
        marker.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(marker, 2);
        grid.Children.Add(marker);

        var content = line.Kind switch
        {
            DiffLineKind.HunkHeader => HunkHeaderContent(line),
            DiffLineKind.FileHeader or DiffLineKind.NoNewline =>
                Mono(line.Text, Resolve("Brush.Text.Disabled")),
            _ => Highlighted(line.Text, language),
        };

        content.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(content, 3);
        grid.Children.Add(content);

        return grid;
    }

    /// <summary>
    /// The <c>@@</c> line, with its stage button when there is one to offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The button lives on the hunk header because that is the row that <i>is</i> the hunk —
    /// a control per changed line would be a control every second row, and there is no such
    /// thing as staging half a hunk anyway.
    /// </para>
    /// <para>
    /// It carries its own index as the command parameter rather than relying on where it sits
    /// in the list. With recycling on, the container this row is built into will later hold a
    /// different line, and a button that worked out its hunk from its position would by then
    /// be pointing at somebody else's.
    /// </para>
    /// </remarks>
    private TextBlock HunkHeaderContent(DiffLine line)
    {
        var header = Mono(line.Text, Resolve("Brush.Accent.Default"));

        if (!ShowHunkActions || HunkCommand is null || line.HunkIndex < 0)
        {
            return header;
        }

        var button = new Button
        {
            Content = HunkActionLabel,
            Command = HunkCommand,
            CommandParameter = line.HunkIndex,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = (double)FindResource("Font.Size.Caption"),
            FontFamily = (FontFamily)FindResource("Font.UI"),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        if (TryFindResource("GhostButton") is Style ghost)
        {
            button.Style = ghost;
        }

        header.Inlines.Add(new InlineUIContainer(button)
        {
            BaselineAlignment = BaselineAlignment.Center,
        });

        return header;
    }

    /// <summary>Build one code line as coloured runs.</summary>
    private TextBlock Highlighted(string text, SyntaxLanguage language)
    {
        var block = Mono(string.Empty, Resolve("Brush.Text.Primary"));

        if (string.IsNullOrEmpty(text))
        {
            // A zero-height row collapses and breaks the alignment of the gutter, so an
            // empty line still needs something with a line box.
            block.Text = " ";
            return block;
        }

        foreach (var span in SyntaxTokenizer.Tokenize(text, language))
        {
            block.Inlines.Add(new Run(text.Substring(span.Start, span.Length))
            {
                Foreground = TokenBrush(span.Kind),
            });
        }

        return block;
    }

    private TextBlock Gutter(int? number, int column)
    {
        var block = Mono(
            number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Resolve("Brush.Text.Disabled"));

        block.TextAlignment = TextAlignment.Right;
        block.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(block, column);
        return block;
    }

    private TextBlock Mono(string text, Brush foreground) => new()
    {
        Text = text,
        Foreground = foreground,
        FontFamily = (FontFamily)FindResource("Font.Mono"),
        FontSize = (double)FindResource("Font.Size.Code"),
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Brush BackgroundFor(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Added => Resolve("Brush.Diff.AddedBg"),
        DiffLineKind.Removed => Resolve("Brush.Diff.DeletedBg"),
        _ => Brushes.Transparent,
    };

    private Brush MarkerBrush(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Added => Resolve("Brush.Status.Added"),
        DiffLineKind.Removed => Resolve("Brush.Status.Deleted"),
        _ => Resolve("Brush.Text.Disabled"),
    };

    private Brush TokenBrush(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => Resolve("Brush.Syntax.Keyword"),
        TokenKind.Type => Resolve("Brush.Syntax.Type"),
        TokenKind.Comment => Resolve("Brush.Syntax.Comment"),
        TokenKind.String => Resolve("Brush.Syntax.String"),
        TokenKind.Number => Resolve("Brush.Syntax.Number"),
        TokenKind.Directive => Resolve("Brush.Syntax.Directive"),
        _ => Resolve("Brush.Text.Primary"),
    };

    private Brush Resolve(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;
}
