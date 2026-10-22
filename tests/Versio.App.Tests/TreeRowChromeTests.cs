using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A tree row says what it is: its kind before the name, its type in words after it.
/// </summary>
/// <remarks>
/// From one look at the running explorer on 2026-09-21: every row in the same face with
/// nothing before its name, and types as raw identifiers in a code font
/// (<c>gvl_persistent</c>, <c>task_configuration</c>).
/// </remarks>
[Collection("theme")]
public sealed class TreeRowChromeTests
{
    private static TreeRow Row(ProjectNodeKind kind, string? type = null, string? typeName = null) =>
        new(new ProjectTreeNode
        {
            Name = "x",
            Path = "plc/x",
            Kind = kind,
            Type = type,
            TypeName = typeName,
        }, depth: 0);

    [Theory]
    [InlineData(ProjectNodeKind.Folder, "Icon.Folder")]
    [InlineData(ProjectNodeKind.Project, "Icon.Project")]
    [InlineData(ProjectNodeKind.Object, "Icon.Code")]
    [InlineData(ProjectNodeKind.Library, "Icon.Library")]
    [InlineData(ProjectNodeKind.Task, "Icon.Task")]
    [InlineData(ProjectNodeKind.Device, "Icon.Device")]
    [InlineData(ProjectNodeKind.File, "Icon.Document")]
    public void Every_kind_of_row_has_a_glyph_the_theme_defines(ProjectNodeKind kind, string key)
    {
        StaHarness.Run(() =>
        {
            Assert.Equal(key, Row(kind).IconKey);
            Assert.IsType<string>(Application.Current.Resources[key]);
        });
    }

    [Fact]
    public void Both_trees_draw_the_glyph_before_the_name()
    {
        // The mapping above is only half; the templates have to ask for it. The project panel
        // and the explorer each carry their own row template, so both are checked.
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();
            var window = new Versio.App.Views.MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            var glyph = (string)Application.Current.Resources["Icon.Code"];

            // Each panel checked while it is the one showing: the rail collapses the others.
            shell.Sync.Tree.Add(Row(ProjectNodeKind.Object, "POU", "POU"));
            Lay(content);
            Assert.Contains(Descendants((DependencyObject)window.FindName("ProjectPanel"))
                .OfType<TextBlock>(), t => t.Text == glyph);

            // The rail's click handler is what flips visibility, and nothing clicks here.
            var explorer = (FrameworkElement)window.FindName("ExplorerPanel");
            explorer.Visibility = Visibility.Visible;
            shell.Explorer.Tree.Add(Row(ProjectNodeKind.Object, "POU", "POU"));
            Lay(content);
            Assert.Contains(Descendants(explorer).OfType<TextBlock>(), t => t.Text == glyph);
        });
    }

    /// <summary>Lay out, let the item generators run, and lay out again.</summary>
    /// <remarks>
    /// A tree's containers are generated from the dispatcher, and with the suite's one STA
    /// thread busy they can land a pass later than the first layout. Alone this test passed
    /// on one layout; in the full suite it looked before the row existed.
    /// </remarks>
    private static void Lay(FrameworkElement content)
    {
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
        content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        content.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            yield return child;

            foreach (var further in Descendants(child))
            {
                yield return further;
            }
        }
    }

    [Fact]
    public void A_deleted_row_draws_its_name_struck_and_a_changed_one_does_not()
    {
        // The owner, 2026-10-22: a red D alone did not say "this property is gone". Through
        // the real template, because the flag is nothing without the trigger that reads it.
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();
            var window = new Versio.App.Views.MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            shell.Sync.Tree.Add(new TreeRow(new ProjectTreeNode
            {
                Name = "P_Busy", Path = "plc/P_Busy", Kind = ProjectNodeKind.Object,
                Status = Versio.Core.Git.ChangeKind.Deleted,
            }, depth: 0));
            shell.Sync.Tree.Add(new TreeRow(new ProjectTreeNode
            {
                Name = "Register", Path = "plc/Register", Kind = ProjectNodeKind.Object,
                Status = Versio.Core.Git.ChangeKind.Modified,
            }, depth: 0));
            Lay(content);

            var names = Descendants((DependencyObject)window.FindName("ProjectPanel")).OfType<TextBlock>().ToList();
            var deleted = Assert.Single(names, t => t.Text == "P_Busy");
            var changed = Assert.Single(names, t => t.Text == "Register");

            Assert.Equal(TextDecorations.Strikethrough, deleted.TextDecorations);
            Assert.True(changed.TextDecorations is null || changed.TextDecorations.Count == 0);
        });
    }

    [Fact]
    public void An_object_row_shows_the_vendor_word_for_its_type_not_the_raw_identifier()
    {
        Assert.Equal("Persistent GVL", Row(ProjectNodeKind.Object, "GVL_PERSISTENT", "Persistent GVL").TypeLabel);

        // A hand-made node with no name resolved still shows something, and never lowercases.
        Assert.Equal("GVL_PERSISTENT", Row(ProjectNodeKind.Object, "GVL_PERSISTENT").TypeLabel);
    }
}
