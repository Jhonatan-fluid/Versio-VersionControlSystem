using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Versio.App.ViewModels;
using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The half of a type link that is XAML: the real window, the real member-table template.
/// </summary>
/// <remarks>
/// The view model can resolve every link correctly while the template binds the hyperlink to
/// a command that does not exist - the binding fails silently and the link does nothing. This
/// realises the window, selects a function block and looks for the link it actually drew.
/// </remarks>
[Collection("theme")]
public sealed class InterfaceLinkWindowTests
{
    private const string Udt = """
        <Document>
          <SW.Types.PlcStruct ID="0">
            <AttributeList>
              <Interface><Sections><Section Name="None"><Member Name="Value" Datatype="Real" /></Section></Sections></Interface>
              <Name>AnaVal</Name>
            </AttributeList>
          </SW.Types.PlcStruct>
        </Document>
        """;

    private const string Fb = """
        <Document>
          <SW.Blocks.FB ID="0">
            <AttributeList>
              <Interface><Sections>
                <Section Name="Input"><Member Name="Enable" Datatype="Bool" /></Section>
                <Section Name="Temp"><Member Name="Level" Datatype="&quot;AnaVal&quot;" /></Section>
              </Sections></Interface>
              <Name>Motor</Name>
            </AttributeList>
          </SW.Blocks.FB>
        </Document>
        """;

    private static void GivenAProject(TempRepo repo)
    {
        Directory.CreateDirectory(Path.Combine(repo.Path, ".versio"));
        File.WriteAllText(Path.Combine(repo.Path, ".versio", "repo.json"), """
            { "schema": 1, "bindings": [ { "id": "b1", "projectPath": "plc/Station.ap21",
              "exportPath": "plc/Station", "vendor": "TiaPortal", "enabled": true } ] }
            """);

        var root = Path.Combine(repo.Path, "plc", "Station");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(repo.Path, "plc", "Station.ap21"), "pointer");
        File.WriteAllText(Path.Combine(root, "_manifest.json"), """
            { "schema": 8, "vendor": "TiaPortal", "bindingId": "b1", "project": "Station.ap21",
              "objectCount": 2, "objects": [
                { "guid": "fb1", "name": "Motor", "type": "FB", "form": "NATIVE", "path": "Motor.xml", "hash": "sha256:0", "bytes": 1 },
                { "guid": "udt1", "name": "AnaVal", "type": "UDT", "form": "NATIVE", "path": "AnaVal.xml", "hash": "sha256:0", "bytes": 1 }
              ], "warnings": [], "skipped": [] }
            """);
        File.WriteAllText(Path.Combine(root, "Motor.xml"), Fb);
        File.WriteAllText(Path.Combine(root, "AnaVal.xml"), Udt);
    }

    [Fact]
    public void The_drawn_type_link_is_wired_to_the_shell_and_carries_the_type()
    {
        StaHarness.Run(async () =>
        {
            using var repo = new TempRepo();
            GivenAProject(repo);

            using var shell = TestShell.Build();
            var window = new MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            await shell.OpenAsync(repo.Path);
            TreeRow? motor = null;
            await Eventually.Until(
                () => (motor = shell.Sync.Tree.SelectMany(Flatten).FirstOrDefault(r => r.Name == "Motor")) is not null,
                "the block is in the tree");
            shell.Sync.SelectedNode = motor;
            shell.ShowRendered = true;

            content.Measure(new Size(1500, 900));
            content.Arrange(new Rect(0, 0, 1500, 900));
            content.UpdateLayout();

            var holders = Descendants(content).OfType<TextBlock>()
                .Where(t => AutomationProperties.GetName(t) == "Open the type" && t.Visibility == Visibility.Visible)
                .ToList();
            var link = Assert.Single(holders.SelectMany(t => t.Inlines.OfType<Hyperlink>()));

            Assert.Same(shell.OpenObjectCommand, link.Command);
            Assert.Equal("udt1", link.CommandParameter);

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
