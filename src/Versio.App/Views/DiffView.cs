using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using Versio.Core.Diff;
using Versio.Core.Iec;

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
// Unsealed for one reason: the row builder runs from PrepareContainerForItemOverride, and a
// ListBox outside a window never realises a container — so the only honest way to test what
// a row PAINTS is a test subclass that calls the builder. See DiffModeTests.Openable.
public class DiffView : ListBox
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

    /// <summary>Old file on the left, new on the right, instead of one interleaved column.</summary>
    public static readonly DependencyProperty SideBySideProperty =
        DependencyProperty.Register(nameof(SideBySide), typeof(bool), typeof(DiffView),
            new PropertyMetadata(false, OnInputChanged));

    /// <summary>
    /// A file to show as itself rather than as a diff. Takes precedence over
    /// <see cref="DiffText"/> when set.
    /// </summary>
    /// <remarks>
    /// Rows instead of a diff string because the Rendered pane is not comparing anything: one
    /// gutter, no marker column, no hunk header, and the declaration/implementation divider
    /// drawn as a divider. The layout — which lines, which numbers — is decided in Core by
    /// <see cref="StSourceLayout"/>, so this control only draws it.
    /// </remarks>
    public static readonly DependencyProperty SourceRowsProperty =
        DependencyProperty.Register(nameof(SourceRows), typeof(IReadOnlyList<SourceRow>),
            typeof(DiffView), new PropertyMetadata(null, OnInputChanged));

    /// <summary>
    /// Whether a compared <see cref="SourceRows"/> file is drawn whole, or folded to its changes
    /// and git's context either side. See <see cref="StSourceLayout.Fold(IReadOnlyList{SourceRow}, int)"/>.
    /// </summary>
    /// <remarks>
    /// True by default so a view nobody binds keeps drawing everything; the pane binds it to its
    /// <i>Show full file</i> box. Raw ignores it: its context comes from git, re-read by the shell.
    /// </remarks>
    public static readonly DependencyProperty ShowFullFileProperty =
        DependencyProperty.Register(nameof(ShowFullFile), typeof(bool), typeof(DiffView),
            new PropertyMetadata(true, OnInputChanged));

    public bool ShowFullFile
    {
        get => (bool)GetValue(ShowFullFileProperty);
        set => SetValue(ShowFullFileProperty, value);
    }

    public IReadOnlyList<SourceRow>? SourceRows
    {
        get => (IReadOnlyList<SourceRow>?)GetValue(SourceRowsProperty);
        set => SetValue(SourceRowsProperty, value);
    }

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

    public bool SideBySide
    {
        get => (bool)GetValue(SideBySideProperty);
        set => SetValue(SideBySideProperty, value);
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

        // SELECTING LINES DID NOTHING AND COPIED NOTHING.
        //
        // Extended selection has been on since this class was written, so click, shift-click
        // and Ctrl-click all built a real selection - and the container template is a bare
        // ContentPresenter with no IsSelected trigger, so nothing was drawn, and no Copy
        // binding existed anywhere in the application, so Ctrl+C did nothing either. A user
        // dragged across twelve lines, saw no response at all, pressed Ctrl+C, and got
        // nothing. Copying a few lines of a changed function into a message asking a
        // colleague for help is one of the most common things anybody does with a diff.
        //
        // A CommandBinding rather than a KeyDown handler: Ctrl+C is already routed to
        // ApplicationCommands.Copy by the input system, and a binding means the same command
        // works from a context menu or a shortcut somebody remaps later.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopy, OnCanCopy));
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = SelectedItems.Count > 0;

    /// <summary>
    /// Put the selected lines on the clipboard, in the order they appear in the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Document order, not selection order.</b> <see cref="Selector.SelectedItems"/> is in
    /// the order things were picked, so Ctrl-clicking upwards through a hunk would paste the
    /// lines reversed — a diff that reads as though the change ran backwards.
    /// </para>
    /// <para>
    /// The marker travels with the line. What gets pasted is an excerpt of a <i>diff</i>, and
    /// stripping the <c>+</c> and <c>-</c> would turn "here is what changed" into "here is
    /// some code" — which is the half that was worth sending.
    /// </para>
    /// <para>
    /// The clipboard genuinely fails: another process can hold it open and
    /// <see cref="Clipboard.SetText(string)"/> then throws. Letting that reach the dispatcher
    /// would take the window down while somebody was trying to copy an error, which is a
    /// memorable way to lose a bug report — the same trap the shell's own copy handler
    /// documents.
    /// </para>
    /// </remarks>
    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (SelectionText() is not { } text)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process has the clipboard. Nothing useful to do, and nothing worth
            // interrupting the user over - the lines are still on screen.
        }

        e.Handled = true;
    }

    /// <summary>What a copy of the current selection pastes, or null when nothing is.</summary>
    /// <remarks>
    /// Separate from the clipboard write so a test can read it: the clipboard is shared with
    /// every other process on the machine, and a test asserting through it measures them too.
    /// </remarks>
    internal string? SelectionText()
    {
        if (SelectedItems.Count == 0)
        {
            return null;
        }

        // Split rows paste as the unified excerpt they stand for: a context row once, an
        // edited pair as its − line then its + line. The pasted text is still a diff —
        // reconstructing the two-column layout in a chat message helps nobody.
        return ItemsSource switch
        {
            IReadOnlyList<DiffLine> lines => CopyText(
                lines.Where(SelectedItems.OfType<DiffLine>().ToHashSet().Contains)),

            IReadOnlyList<SideBySideRow> rows => CopyText(
                rows.Where(SelectedItems.OfType<SideBySideRow>().ToHashSet().Contains)
                    .SelectMany(row =>
                        row.Spanning is { } spanning ? new[] { spanning }
                        : ReferenceEquals(row.Left, row.Right) ? new[] { row.Left! }
                        : new[] { row.Left, row.Right }.OfType<DiffLine>().ToArray())),

            // A file shown as itself pastes as the file: no markers, and a divider as the
            // delimiter line it stands for — so an excerpt spanning both halves still pastes
            // as valid .st. The synthetic "Declaration" divider stands for no line and pastes
            // nothing, and neither does a removed line: it is the committed file's, not this one's.
            // Side by side copies the working side, the file as it is now.
            IReadOnlyList<SourceSplitRow> pairs => string.Join(Environment.NewLine,
                pairs.Where(SelectedItems.OfType<SourceSplitRow>().ToHashSet().Contains)
                    .Select(pair => pair.Divider?.FileText ?? pair.Right?.Text)
                    .OfType<string>()),

            IReadOnlyList<SourceRow> source => string.Join(Environment.NewLine,
                source.Where(SelectedItems.OfType<SourceRow>().ToHashSet().Contains)
                    .Select(row => row switch
                    {
                        SourceCodeLine line => line.Text,
                        SourceDivider divider => divider.FileText,
                        _ => null,
                    })
                    .OfType<string>()),

            _ => null,
        };
    }

    /// <summary>Document order and the marker on every line — the excerpt is still a diff.</summary>
    private static string CopyText(IEnumerable<DiffLine> lines) =>
        string.Join(Environment.NewLine, lines.Select(line => line.Marker + line.Text));

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DiffView)d).Rebuild();

    // Set before ItemsSource, so a container can never be prepared without one.
    private SyntaxLanguage _language = LanguageCatalog.ForPath(null);

    /// <summary>
    /// Each line's own file's language, when the document holds more than one file.
    /// </summary>
    /// <remarks>
    /// A commit's patch is many files under one pane path - the commit's caption - so the one
    /// language the view had came from a name with no extension and nothing was highlighted.
    /// Keyed by reference: lines are records, and two identical context lines in two files are
    /// equal by value. Null for a single file, where <see cref="_language"/> already is it.
    /// </remarks>
    private IReadOnlyDictionary<DiffLine, SyntaxLanguage>? _fileLanguages;

    private SyntaxLanguage LanguageFor(DiffLine? line) =>
        line is not null && _fileLanguages is { } map && map.TryGetValue(line, out var language)
            ? language
            : _language;

    /// <summary>Which file each line belongs to, as a language — only worth it for several files.</summary>
    private static IReadOnlyDictionary<DiffLine, SyntaxLanguage>? LanguagesByFile(DiffDocument document)
    {
        if (document.Files.Count < 2)
        {
            return null;
        }

        var map = new Dictionary<DiffLine, SyntaxLanguage>(
            document.Lines.Count, ReferenceEqualityComparer.Instance);
        var current = LanguageCatalog.ForPath(null);

        foreach (var line in document.Lines)
        {
            if (line.Title is { } title)
            {
                current = Versio.Core.Plc.PlcFormats.LanguageFor(title.Path);
            }

            map[line] = current;
        }

        return map;
    }

    /// <summary>Guards against a slow parse landing after a newer selection.</summary>
    private int _generation;

    /// <summary>
    /// Unified source rows: the working file alone, removed lines left to side by side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner, 2026-10-22: the removed lines belong only in the side-by-side view. Drawn in
    /// place, struck, they doubled every edited line — the old text and the new one stacked, the
    /// method reading as twice its length — on the tab whose job is to show the file as it is.
    /// The two columns are where old and new are read against each other.
    /// </para>
    /// <para>
    /// <b>The place is still marked.</b> A change that only deleted lines would otherwise leave
    /// nothing on this tab at all, and an object badged M with no visible change reads as a
    /// fault. The line that now stands where they were carries a red rule on its top edge (on
    /// its bottom edge when they were the last lines), and its tooltip says how many went.
    /// </para>
    /// </remarks>
    private List<SourceRow> Unified(IReadOnlyList<SourceRow> marked)
    {
        var rows = new List<SourceRow>(marked.Count);
        var near = new Dictionary<SourceRow, (int Count, bool After)>(ReferenceEqualityComparer.Instance);
        var pending = 0;

        foreach (var row in marked)
        {
            if (row is SourceRemovedLine)
            {
                pending++;
                continue;
            }

            if (pending > 0 && row is SourceCodeLine)
            {
                near[row] = (pending, false);
                pending = 0;
            }

            rows.Add(row);
        }

        if (pending > 0 && rows.LastOrDefault(r => r is SourceCodeLine) is { } last)
        {
            near[last] = (pending, true);
        }

        _removedNear = near;
        return rows;
    }

    /// <summary>Unified rows that stand where removed lines were, by reference. See <see cref="Unified"/>.</summary>
    private Dictionary<SourceRow, (int Count, bool After)>? _removedNear;

    private async void Rebuild()
    {
        var text = DiffText;
        var path = FilePath;
        var split = SideBySide;
        var generation = ++_generation;

        // A vendor names its own extensions (a .gvl is Structured Text); the catalog the rest.
        _language = Versio.Core.Plc.PlcFormats.LanguageFor(path);
        _fileLanguages = null;

        if (SourceRows is { } source)
        {
            // Nothing to parse, so nothing to put off the dispatcher. The generation bump
            // above still matters: it stops a diff parse already in flight from landing on
            // top of these rows. Stretch rather than Left so a divider runs the full width
            // of the pane; source rows carry no tint for a stretched row to smear.
            // Side by side: the committed version on the left, the working one on the right, as Raw
            // and the ladder already draw it. Only worth it when something is marked; an
            // unchanged file would be the same text twice.
            var marked = source.Any(r => r is SourceRemovedLine or SourceCodeLine { Change: SourceChange.Added });
            var full = ShowFullFile;
            ScrollViewer.SetHorizontalScrollBarVisibility(
                this, split && marked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _removedNear = null;

            if (split && marked)
            {
                var pairs = StSourceLayout.SideBySide(source);
                ItemsSource = full ? pairs : StSourceLayout.Fold(pairs);
                return;
            }

            ItemsSource = marked ? Unified(full ? source : StSourceLayout.Fold(source)) : source;
            return;
        }

        // Rows own their width in split mode. Unified rows are content-sized and the list
        // scrolls sideways; split rows stretch to the viewport and give each file half,
        // because two independently content-sized columns would land at a different split
        // on every row.
        ScrollViewer.SetHorizontalScrollBarVisibility(
            this, split ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        HorizontalContentAlignment = split
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        if (string.IsNullOrEmpty(text))
        {
            ItemsSource = null;
            return;
        }

        // Off the dispatcher. Parsing is linear and quick per line, but a 2.5 MB diff is
        // still tens of thousands of them, and none of that work needs the UI thread.
        // The emphasis pass and the split fold ride along: both are linear too, and both
        // exist so a row can be BUILT cheaply when it is realised.
        var (document, rows, languages) = await Task.Run(() =>
        {
            var parsed = IntralineEmphasis.Annotate(UnifiedDiffParser.Parse(text));
            return (parsed, split ? SideBySideDiff.Build(parsed) : null, LanguagesByFile(parsed));
        }).ConfigureAwait(true);

        // A user clicking down a list faster than the parses complete would otherwise see
        // an earlier, slower file win and replace the one they are looking at.
        if (generation != _generation)
        {
            return;
        }

        // Before ItemsSource, for the same reason _language is: a container prepared by the
        // assignment below must find its file's language already there.
        _fileLanguages = languages;

        ItemsSource = document.IsEmpty
            ? null
            : rows is not null ? rows : document.Lines;
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

        if (element is not ContentControl container)
        {
            return;
        }

        container.Content = item switch
        {
            DiffLine line => BuildRow(line, LanguageFor(line)),
            SideBySideRow row => BuildSplitRow(row, LanguageFor(row.Left ?? row.Right ?? row.Spanning)),
            SourceCodeLine line => BuildSourceRow(line, _language),
            SourceRemovedLine removed => BuildRemovedSourceRow(removed, _language),
            SourceSplitRow pair => pair.Divider is { } spanning ? BuildDivider(spanning)
                : pair.Fold is { } folded ? BuildFold(folded)
                : BuildSourceSplitRow(pair, _language),
            SourceDivider divider => BuildDivider(divider),
            SourceFold fold => BuildFold(fold),
            _ => container.Content,
        };
    }

    /// <summary>One line of a file shown as itself: one gutter, then the code.</summary>
    /// <remarks>
    /// The diff row's second gutter and marker column are gone because on a file that is not
    /// being compared they only ever held a copy of the first number and a blank. The gutter
    /// keeps the diff row's width so switching between Raw and Rendered does not shift the
    /// code sideways under the reader's eye.
    /// </remarks>
    private UIElement BuildSourceRow(SourceCodeLine line, SyntaxLanguage language)
    {
        // A line new since the last commit carries the diff's own added tint, so the Rendered
        // tab and Raw agree on what changed (the owner, 2026-10-10: a file marked M rendered as
        // if untouched).
        var grid = new Grid
        {
            Background = line.Change == SourceChange.Added ? Resolve("Brush.Diff.AddedBg") : Brushes.Transparent,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Gutter(line.Number, 0));

        var content = Highlighted(line.Text, language);
        content.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        // Where removed lines stood, in a view that does not draw them. An overlay rather than
        // a border, so the marked line is not two pixels taller than its neighbours.
        if (_removedNear is { } near && near.TryGetValue(line, out var removed))
        {
            var rule = new System.Windows.Shapes.Rectangle
            {
                Height = 2,
                Fill = Resolve("Brush.Status.Deleted"),
                VerticalAlignment = removed.After ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            Grid.SetColumnSpan(rule, 2);
            grid.Children.Add(rule);

            var count = removed.Count.ToString(CultureInfo.InvariantCulture);
            grid.ToolTip = $"{count} {(removed.Count == 1 ? "line" : "lines")} removed "
                           + $"{(removed.After ? "below" : "above")} since the last commit. Side by side shows them.";
        }

        return grid;
    }

    /// <summary>Unchanged lines folded away: a quiet row saying how many, and where.</summary>
    private UIElement BuildFold(SourceFold fold)
    {
        var grid = new Grid { Background = Resolve("Brush.Surface.Sunken"), Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = Mono("⋯", Resolve("Brush.Text.Secondary"));
        marker.TextAlignment = TextAlignment.Right;
        marker.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(marker);

        var label = new TextBlock
        {
            Text = $"{fold.Hidden.ToString(CultureInfo.InvariantCulture)} unchanged {(fold.Hidden == 1 ? "line" : "lines")}",
            Foreground = Resolve("Brush.Text.Secondary"),
            FontFamily = (FontFamily)FindResource("Font.UI"),
            FontSize = (double)FindResource("Font.Size.Caption"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 12, 0),
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        grid.ToolTip = "Tick Show full file to see every line";
        return grid;
    }

    /// <summary>
    /// A line the last commit had and the file no longer does: no number (it has none in this
    /// file), the diff's removed tint, and struck, where it stood.
    /// </summary>
    private UIElement BuildRemovedSourceRow(SourceRemovedLine line, SyntaxLanguage language)
    {
        var grid = new Grid { Background = Resolve("Brush.Diff.DeletedBg") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var marker = Mono("\u2212", Resolve("Brush.Status.Deleted"));
        marker.TextAlignment = TextAlignment.Right;
        marker.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(marker);

        // The strike starts where the text does. Decorating the whole line drew the leading
        // tabs as one long bar — a removed "\t\t;" read as a redaction mark, not as an
        // indented semicolon (seen on a real library, 2026-10-14). The indent is kept, and
        // kept undecorated, so the removed line still stands at its own depth.
        var indentLength = line.Text.Length - line.Text.TrimStart(' ', '\t').Length;
        var content = Highlighted(line.Text[indentLength..], language);
        foreach (var inline in content.Inlines)
        {
            inline.TextDecorations = TextDecorations.Strikethrough;
        }

        if (indentLength > 0)
        {
            var indent = new System.Windows.Documents.Run(line.Text[..indentLength]);
            if (content.Inlines.FirstInline is { } first)
            {
                content.Inlines.InsertBefore(first, indent);
            }
            else
            {
                content.Inlines.Add(indent);
            }
        }

        content.Margin = new Thickness(6, 0, 12, 0);
        content.ToolTip = $"Removed since the last commit (was line {line.OldFileLine.ToString(CultureInfo.InvariantCulture)})";
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        return grid;
    }

    /// <summary>
    /// One side-by-side source row: the committed line, a rule, the working line, each tinted as
    /// Raw's split rows are and numbered in its own version.
    /// </summary>
    private UIElement BuildSourceSplitRow(SourceSplitRow pair, SyntaxLanguage language)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var divider = new Border { Width = 1, Background = Resolve("Brush.Border.Subtle") };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        var left = SourceSplitCell(pair.Left, language);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = SourceSplitCell(pair.Right, language);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    private Grid SourceSplitCell(SourceSplitCell? cell, SyntaxLanguage language)
    {
        var grid = new Grid
        {
            Background = cell?.Kind switch
            {
                SourceSide.Added => Resolve("Brush.Diff.AddedBg"),
                SourceSide.Removed => Resolve("Brush.Diff.DeletedBg"),
                _ => Brushes.Transparent,
            },
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (cell is null)
        {
            return grid;
        }

        grid.Children.Add(Gutter(cell.Number, 0));

        var content = Highlighted(cell.Text, language);
        content.Margin = new Thickness(6, 0, 12, 0);

        // Clipped rather than scrolled, as Raw's split rows are: the halves share the viewport.
        content.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>The boundary between declaration and implementation, drawn as one.</summary>
    /// <remarks>
    /// The label sits in the code column, not the gutter, so it lines up with the code it
    /// names; the rule runs from its end to the pane's edge. It is the reason the numbers
    /// below it start at 1 again, so it has to be seen — Secondary rather than Disabled —
    /// without competing with the code: UI font, caption size, no fill.
    /// </remarks>
    private UIElement BuildDivider(SourceDivider divider)
    {
        var grid = new Grid
        {
            // Air above a divider that follows code. The one divider standing for no file
            // line is the Declaration label at the very top, which needs none.
            Margin = new Thickness(0, divider.FileText is null ? 2 : 10, 0, 4),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        var label = new TextBlock
        {
            Text = divider.Label.ToUpperInvariant(),
            Foreground = Resolve("Brush.Text.Secondary"),
            FontFamily = (FontFamily)FindResource("Font.UI"),
            FontSize = (double)FindResource("Font.Size.Caption"),
            FontWeight = (FontWeight)FindResource("Font.Weight.Semibold"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0),
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var rule = new Border
        {
            Height = 1,
            Background = Resolve("Brush.Border.Subtle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(rule, 2);
        grid.Children.Add(rule);

        return grid;
    }

    /// <summary>One split row: the old file's cell, a divider, the new file's cell.</summary>
    /// <remarks>
    /// A spanning line — header, hunk marker — keeps the unified row builder, buttons and
    /// all, because splitting a hunk header in two would be two half-sentences. A blank
    /// cell keeps its side's tint off entirely: a deletion has no counterpart on the right,
    /// and tinting the void green would claim an addition that never happened.
    /// </remarks>
    private UIElement BuildSplitRow(SideBySideRow row, SyntaxLanguage language)
    {
        if (row.Spanning is { } spanning)
        {
            return BuildRow(spanning, language);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        var divider = new Border
        {
            Width = 1,
            Background = Resolve("Brush.Border.Subtle"),
        };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        var left = SplitCell(row.Left, old: true, language);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = SplitCell(row.Right, old: false, language);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    private UIElement SplitCell(DiffLine? line, bool old, SyntaxLanguage language)
    {
        var cell = new Grid
        {
            Background = line is null ? Brushes.Transparent : BackgroundFor(line),
        };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        cell.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        if (line is null)
        {
            return cell;
        }

        cell.Children.Add(Gutter(old ? line.OldLine : line.NewLine, 0));

        var content = Highlighted(line.Text, language, line.Emphasis, EmphasisBrush(line));

        // Clipped rather than scrolled: the two halves share the viewport, and a long line
        // is what the unified mode is one click away for.
        content.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(content, 1);
        cell.Children.Add(content);

        return cell;
    }

    private UIElement BuildRow(DiffLine line, SyntaxLanguage language)
    {
        if (line is { Kind: DiffLineKind.FileTitle, Title: { } title })
        {
            return BuildFileTitle(title);
        }

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
            _ => Highlighted(line.Text, language, line.Emphasis, EmphasisBrush(line)),
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
    /// <remarks>
    /// Emphasis is a background on the runs inside it — the syntax colours stay, because
    /// the change and the code are two facts about the same characters and losing either
    /// to show the other would be a regression. A syntax token that straddles an emphasis
    /// boundary is split at it: WPF backgrounds are per-run, so the boundary has to be one.
    /// </remarks>
    private TextBlock Highlighted(
        string text,
        SyntaxLanguage language,
        IReadOnlyList<TextSpan>? emphasis = null,
        Brush? emphasisBrush = null)
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
            foreach (var (start, length, emphasised) in
                     SplitAtEmphasis(span.Start, span.Length, emphasis))
            {
                block.Inlines.Add(new Run(text.Substring(start, length))
                {
                    Foreground = TokenBrush(span.Kind),
                    Background = emphasised && emphasisBrush is not null
                        ? emphasisBrush
                        : null,
                });
            }
        }

        return block;
    }

    /// <summary>Cut one syntax token at the emphasis boundaries crossing it.</summary>
    private static IEnumerable<(int Start, int Length, bool Emphasised)> SplitAtEmphasis(
        int start, int length, IReadOnlyList<TextSpan>? emphasis)
    {
        if (emphasis is not { Count: > 0 })
        {
            yield return (start, length, false);
            yield break;
        }

        var at = start;
        var end = start + length;

        foreach (var span in emphasis)
        {
            if (span.End <= at || span.Start >= end)
            {
                continue;
            }

            if (span.Start > at)
            {
                yield return (at, span.Start - at, false);
                at = span.Start;
            }

            var stop = Math.Min(span.End, end);
            yield return (at, stop - at, true);
            at = stop;
        }

        if (at < end)
        {
            yield return (at, end - at, false);
        }
    }

    private Brush? EmphasisBrush(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Added => Resolve("Brush.Diff.AddedWord"),
        DiffLineKind.Removed => Resolve("Brush.Diff.DeletedWord"),
        _ => null,
    };

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

    /// <summary>
    /// One file's heading: what happened to it, its path, and its own +/− counts.
    /// </summary>
    /// <remarks>
    /// Stands where <c>diff --git</c>, <c>index</c>, <c>---</c> and <c>+++</c> used to, dimmed,
    /// four lines per file — so in a commit of a dozen files the boundaries were the hardest
    /// thing on screen to find. A sunken band with space above it, the path in the body's
    /// weight, and the counts in the diff's own colours, so a reader scrolling a long commit
    /// sees where each file starts and how much of it changed before reading any of it.
    /// </remarks>
    private Border BuildFileTitle(DiffFileTitle title)
    {
        var line = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = (double)FindResource("Font.Size.Code"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var (word, brush) = title.Change switch
        {
            DiffFileChange.Added => ("added", "Brush.Status.Added"),
            DiffFileChange.Deleted => ("deleted", "Brush.Status.Deleted"),
            DiffFileChange.Renamed => ("renamed", "Brush.Status.Renamed"),
            _ => (string.Empty, "Brush.Text.Secondary"),
        };

        if (word.Length > 0)
        {
            line.Inlines.Add(new Run(word + "  ") { Foreground = Resolve(brush) });
        }

        if (title.OldPath is { } from)
        {
            line.Inlines.Add(new Run(from + " → ") { Foreground = Resolve("Brush.Text.Secondary") });
        }

        line.Inlines.Add(new Run(title.Path)
        {
            Foreground = Resolve("Brush.Text.Primary"),
            FontWeight = (FontWeight)FindResource("Font.Weight.Semibold"),
        });

        if (title.Added > 0)
        {
            line.Inlines.Add(new Run($"   +{title.Added}") { Foreground = Resolve("Brush.Status.Added") });
        }

        if (title.Removed > 0)
        {
            line.Inlines.Add(new Run($"   −{title.Removed}") { Foreground = Resolve("Brush.Status.Deleted") });
        }

        return new Border
        {
            Background = Resolve("Brush.Surface.Sunken"),
            Margin = new Thickness(0, 10, 0, 2),
            Padding = new Thickness(12, 4, 12, 4),
            Child = line,
        };
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
