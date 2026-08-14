using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A changed file shown as the object it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows are realised, not inspected.</b> The lesson this panel has taught three times: a
/// <c>StaticResource</c> inside a template resolves on instantiation, and a binding to a
/// property that does not exist renders nothing at all — silently. A test that constructs the
/// window and reads the template proves neither.
/// </para>
/// <para>
/// The view-model half is checked without a window where it can be, because the rule under test
/// — a path with no object behind it is shown as a path — is a rule, and it is cheaper to break
/// deliberately when it is not wrapped in a visual tree.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class PlcAwareRowTests
{
    private static (MainWindow Window, ShellViewModel Shell) Build()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var shell = new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
        return (new MainWindow(shell), shell);
    }

    private static (Border Panel, FrameworkElement Content) ShowGitPanel(MainWindow window)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        var rail = Assert.IsType<RadioButton>(window.FindName("RailGit"));
        rail.IsChecked = true;

        var panel = Assert.IsType<Border>(window.FindName("GitPanel"));

        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();

        return (panel, content);
    }

    private static ChangeRow Row(string path, ChangeKind kind = ChangeKind.Modified) =>
        new(new FileChange
        {
            Path = path,
            Staged = ChangeKind.Unmodified,
            Unstaged = kind,
        });

    private static NamedObject Named(string name, string type, bool exportOnly = false) =>
        new()
        {
            Name = name,
            Type = type,
            Guid = "g1",
            Project = "Machine",
            IsExportOnly = exportOnly,
        };

    // ---- the row, without a window -----------------------------------------------------

    [Fact]
    public void An_unnamed_row_leads_with_its_filename_and_not_its_whole_path()
    {
        var row = Row("src/Project/PLC/POU/Application/Notes.txt");

        // The defect this replaces: one line of path, trimmed at the TAIL, so a 380 px column
        // showed "src/Project/PLC/POU/Applic…" on every row.
        Assert.Equal("Notes.txt", row.Primary);
        Assert.Equal("src/Project/PLC/POU/Application", row.Secondary);
        Assert.False(row.IsObject);
        Assert.Equal(string.Empty, row.TypeLabel);
    }

    [Fact]
    public void A_named_row_leads_with_the_object_and_keeps_the_layer_beneath_it()
    {
        var row = Row("plc/Machine/App/Glob_Var.gvl.st");
        row.Describe(Named("Glob_Var", "GVL"));

        Assert.Equal("Glob_Var", row.Primary);
        Assert.Equal("GVL", row.TypeLabel);
        Assert.True(row.IsObject);

        // The file name stays on the second line: one GVL is two files, and two rows both
        // reading "Glob_Var" over the same directory would be indistinguishable.
        Assert.Equal("plc/Machine/App/Glob_Var.gvl.st", row.Secondary);
    }

    [Fact]
    public void Naming_a_row_is_reversible_and_raises_for_every_line_it_moves()
    {
        var row = Row("plc/Machine/App/FB_Motor.st");
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        row.Describe(Named("FB_Motor", "POU"));

        Assert.Contains(nameof(ChangeRow.Primary), raised);
        Assert.Contains(nameof(ChangeRow.Secondary), raised);
        Assert.Contains(nameof(ChangeRow.TypeLabel), raised);
        Assert.Contains(nameof(ChangeRow.IsObject), raised);

        // A repository that closes takes its names with it.
        row.Describe(null);
        Assert.False(row.IsObject);
        Assert.Equal("FB_Motor.st", row.Primary);
    }

    [Fact]
    public void Describing_a_row_with_what_it_already_has_raises_nothing()
    {
        // Called on every refresh, and a refresh fires whenever the working tree moves. A
        // property change per row per keystroke would relayout the list while somebody reads it.
        var row = Row("plc/Machine/App/FB_Motor.st");
        var found = Named("FB_Motor", "POU");
        row.Describe(found);

        var raised = 0;
        row.PropertyChanged += (_, _) => raised++;
        row.Describe(found);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void An_export_only_row_says_so()
    {
        var row = Row("plc/Machine/App/Library Manager.libraries.yaml");
        row.Describe(Named("Library Manager", "LIBRARY_MANAGER", exportOnly: true));

        Assert.True(row.IsExportOnly);
        Assert.False(Row("plc/Machine/App/FB_Motor.st").IsExportOnly);
    }

    // ---- the row, realised -------------------------------------------------------------

    [Fact]
    public void A_realised_row_shows_the_object_name_and_its_type_chip()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            var row = Row("plc/Machine/App/FB_Motor.st");
            row.Describe(Named("FB_Motor", "POU"));
            shell.Unstaged.Add(row);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            // Located by binding path, not by position: a ListBoxItem's own chrome carries the
            // same DataContext, so "the first Border under a row" is whatever WPF built first.
            Assert.Equal("FB_Motor", BoundText(panel, nameof(ChangeRow.Primary)));
            Assert.Equal("POU", BoundText(panel, nameof(ChangeRow.TypeLabel)));
            Assert.Equal("plc/Machine/App/FB_Motor.st",
                BoundText(panel, nameof(ChangeRow.Secondary)));

            // The whole path is still reachable — it is what git commits, and somebody will
            // want to copy it.
            Assert.Contains(AllOf<FrameworkElement>(panel),
                e => (e.ToolTip as string) == "plc/Machine/App/FB_Motor.st");
        });
    }

    [Fact]
    public void A_realised_row_that_is_not_an_object_shows_no_chip()
    {
        // A blank chip is a control that looks broken, and most of a repository is not PLC.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            shell.Unstaged.Add(Row("README.md"));

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var chip = BoundBlock(panel, nameof(ChangeRow.TypeLabel));
            var border = Assert.IsType<Border>(VisualTreeHelper.GetParent(chip));

            Assert.Equal(Visibility.Collapsed, border.Visibility);
            Assert.Equal("README.md", BoundText(panel, nameof(ChangeRow.Primary)));
        });
    }

    // ---- the summary line and the draft -------------------------------------------------

    [Fact]
    public void The_summary_line_is_hidden_when_there_is_nothing_to_say()
    {
        StaHarness.Run(() =>
        {
            var (_, shell) = Build();

            Assert.Equal(string.Empty, shell.ChangeSummaryLine);
            Assert.False(shell.HasChangeSummary);
        });
    }

    [Fact]
    public void The_draft_button_is_dead_while_the_box_has_words_in_it()
    {
        StaHarness.Run(() =>
        {
            var (_, shell) = Build();

            // Nothing changed: nothing to draft either way.
            Assert.False(shell.DraftCommitMessageCommand.CanExecute(null));

            shell.CommitMessage = "half a sentence somebody is still writing";
            Assert.False(shell.DraftCommitMessageCommand.CanExecute(null));

            // And pressing it anyway - which the UI will not let you do - leaves the words be.
            shell.DraftCommitMessageCommand.Execute(null);
            Assert.Equal("half a sentence somebody is still writing", shell.CommitMessage);
        });
    }

    /// <summary>The one TextBlock whose Text is bound to this property of the row.</summary>
    private static TextBlock BoundBlock(DependencyObject root, string path) =>
        AllOf<TextBlock>(root).Single(t =>
            t.DataContext is ChangeRow &&
            BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == path);

    private static string BoundText(DependencyObject root, string path) =>
        BoundBlock(root, path).Text;

    private static IEnumerable<T> AllOf<T>(DependencyObject root)
        where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node is T wanted)
            {
                yield return wanted;
            }

            var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
