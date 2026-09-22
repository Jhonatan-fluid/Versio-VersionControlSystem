using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Versio.App.Views;
using Versio.Core.Diff;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A commit reads as files: one heading per file, and each file's code in its own language.
/// </summary>
/// <remarks>
/// Seen in the running window on 2026-09-21: a commit's patch as raw <c>git show</c> text,
/// four dimmed header lines per file, and no highlighting at all, because the pane's path was
/// the commit's caption and a caption has no extension.
/// </remarks>
[Collection("theme")]
public sealed class DiffFileTitleRowTests
{
    private const string Commit = """
        diff --git a/plc/FB_Motor.st b/plc/FB_Motor.st
        index 1..2 100644
        --- a/plc/FB_Motor.st
        +++ b/plc/FB_Motor.st
        @@ -1 +1,2 @@
         FUNCTION_BLOCK FB_Motor
        +VAR END_VAR
        diff --git a/docs/NOTES.md b/docs/NOTES.md
        new file mode 100644
        index 0000000..3
        --- /dev/null
        +++ b/docs/NOTES.md
        @@ -0,0 +1 @@
        +notes
        """;

    private sealed class Openable : DiffView
    {
        public UIElement RowFor(object item)
        {
            var container = new ListBoxItem();
            PrepareContainerForItemOverride(container, item);
            return Assert.IsAssignableFrom<UIElement>(container.Content);
        }
    }

    private static async Task<Openable> Loaded(string caption)
    {
        var view = new Openable { FilePath = caption, DiffText = Commit };
        for (var attempt = 0; attempt < 200 && view.ItemsSource is null; attempt++)
        {
            await Task.Delay(5);
        }

        return view;
    }

    [Fact]
    public void Each_file_opens_with_a_heading_naming_it_and_its_counts()
    {
        StaHarness.Run(async () =>
        {
            var view = await Loaded("0c742a4 A commit");

            var titles = view.Items.OfType<DiffLine>().Where(l => l.Kind == DiffLineKind.FileTitle).ToList();
            Assert.Equal(2, titles.Count);

            var row = Assert.IsType<Border>(view.RowFor(titles[1]));
            var text = string.Concat(((TextBlock)row.Child).Inlines.OfType<Run>().Select(r => r.Text));

            Assert.Contains("added", text);
            Assert.Contains("docs/NOTES.md", text);
            Assert.Contains("+1", text);
        });
    }

    [Fact]
    public void Each_file_is_highlighted_in_its_own_language_under_a_caption_with_none()
    {
        StaHarness.Run(async () =>
        {
            var view = await Loaded("0c742a4 A commit");
            var keyword = ((SolidColorBrush)Application.Current.Resources["Brush.Syntax.Keyword"]).Color;

            var st = view.Items.OfType<DiffLine>().First(l => l is { Kind: DiffLineKind.Context });
            var row = Assert.IsType<Grid>(view.RowFor(st));
            var code = row.Children.OfType<TextBlock>().Last();

            Assert.Contains(code.Inlines.OfType<Run>(),
                r => r.Text == "FUNCTION_BLOCK" && r.Foreground is SolidColorBrush b && b.Color == keyword);
        });
    }
}
