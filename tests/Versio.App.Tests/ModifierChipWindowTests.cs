using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The half of a header modifier chip that is XAML: the real window, the real member-table
/// template, a method whose header gained <c>PUBLIC</c>.
/// </summary>
/// <remarks>
/// FINDINGS 2026-10-28: <c>FB_Template.Reset</c> gained <c>PUBLIC</c>, the tree said M, Raw showed
/// it, and the Rendered tab drew an empty interface over unchanged code. The reader, the diff and
/// the HTML are pinned in Core; what only this can pin is the <c>ItemsControl</c> docked after the
/// name in <c>MainWindow.xaml</c>, which the App suite otherwise never realises. Delete it and the
/// asserts past the view model go red.
/// </remarks>
[Collection("theme")]
public sealed class ModifierChipWindowTests
{
    /// <summary>A source project of one method, its header as given; the same shape <c>MemberTablePaneFlowTests</c> uses.</summary>
    private static void GivenAMethod(TempRepo repo, string header)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);
        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            { "id": "b1", "projectPath": "plc/Rig.project", "exportPath": "plc/Rig", "enabled": true }
          ]
        }
        """);

        var application = Path.Combine(repo.Path, "plc", "Rig", "Application");
        Directory.CreateDirectory(application);
        File.WriteAllBytes(Path.Combine(repo.Path, "plc", "Rig.project"), [0x00, 0x01]);
        File.WriteAllText(Path.Combine(repo.Path, "plc", "Rig", "_manifest.json"), """
        {
          "schema": 4,
          "bindingId": "b1",
          "project": "Rig.project",
          "objectCount": 1,
          "objects": [
            { "guid": "m1", "name": "DoIt", "type": "METHOD", "form": "ST_FULL",
              "path": "Application/DoIt.st", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);
        File.WriteAllText(Path.Combine(application, "DoIt.st"),
            header + "\nVAR_INPUT\n\tx : INT;\nEND_VAR\n// --- BEGIN IMPLEMENTATION ---\nx := 1;\n");
    }

    [Fact]
    public void A_method_that_gained_PUBLIC_draws_a_green_chip_after_its_name()
    {
        StaHarness.Run(async () =>
        {
            using var repo = new TempRepo(identity: true);
            GivenAMethod(repo, "METHOD DoIt");
            repo.Git("add -A");
            repo.Git("commit -qm base");
            GivenAMethod(repo, "METHOD PUBLIC DoIt");

            using var shell = TestShell.Build();
            var window = new MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            await shell.OpenAsync(repo.Path);
            TreeRow? method = null;
            await Eventually.Until(
                () => (method = shell.Sync.Tree.SelectMany(Flatten).FirstOrDefault(r => r.Name == "DoIt")) is not null,
                "the method is in the tree");
            shell.Sync.SelectedNode = method;
            shell.ShowRendered = true;

            // The mark arrives once the committed copy has been read and compared.
            await Eventually.Until(
                () => shell.RenderedContent is InterfaceRender { Table.Modifiers: [{ Name: "PUBLIC", Change: MemberChange.Added }] },
                "the gained PUBLIC was marked on the table");

            content.Measure(new Size(1500, 900));
            content.Arrange(new Rect(0, 0, 1500, 900));
            content.UpdateLayout();

            // Drawn, visible, after the name, and green: the change never rests on the view
            // model alone, because a binding to a property that is not there fails silently.
            var strip = Assert.Single(Descendants(content).OfType<ItemsControl>(),
                i => AutomationProperties.GetName(i) == "Modifiers on this object's header" && i.Visibility == Visibility.Visible);
            var chip = Assert.Single(Descendants(strip).OfType<TextBlock>(), t => t.Text == "PUBLIC");
            var added = Assert.IsType<SolidColorBrush>(window.FindResource("Brush.Status.Added"));
            Assert.Equal(added.Color, Assert.IsType<SolidColorBrush>(chip.Foreground).Color);

            var name = Assert.Single(Descendants(content).OfType<TextBlock>(),
                t => t.Text == "DoIt" && t.FontWeight == (FontWeight)window.FindResource("Font.Weight.Semibold"));
            // Relative to the content root: no window is shown, so there is no screen to point to.
            var chipAt = chip.TransformToAncestor(content).Transform(default);
            var nameAt = name.TransformToAncestor(content).Transform(default);
            Assert.True(chipAt.X > nameAt.X + name.ActualWidth, "the chip is not after the name");
            Assert.True(Math.Abs(chipAt.Y - nameAt.Y) < name.ActualHeight, "the chip is not on the name's line");

            if (Environment.GetEnvironmentVariable("SNAP_OUT") is { Length: > 0 } snap)
            {
                var bmp = new RenderTargetBitmap(1500, 900, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(content);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bmp));
                using var file = File.Create(snap);
                png.Save(file);
            }
        });
    }

    private static IEnumerable<TreeRow> Flatten(TreeRow row) =>
        new[] { row }.Concat(row.Children.SelectMany(Flatten));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var further in Descendants(child))
            {
                yield return further;
            }
        }
    }
}
