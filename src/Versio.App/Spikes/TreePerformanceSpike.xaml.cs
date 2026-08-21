using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Versio.App.Theme;

namespace Versio.App.Spikes;

/// <summary>
/// Node model for the spike. Deliberately the same shape the real tree will use — glyph,
/// label, status, roll-up badge — so the measurement reflects the real render cost rather
/// than a stripped-down placeholder.
/// </summary>
public sealed class SpikeNode
{
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;
    public List<SpikeNode> Children { get; } = [];

    /// <summary>
    /// Expanded by default, because a collapsed tree measures nothing.
    /// </summary>
    /// <remarks>
    /// The first run of this spike reported 13 realised containers for BOTH trees and
    /// declared a pass. Only the roots were ever visible, so neither tree was stressed and
    /// the comparison could not have failed. Expanding is what makes the numbers mean
    /// something — and it matches the real UI, where "changed only" mode shows an expanded
    /// tree of everything that differs.
    /// </remarks>
    public bool IsExpanded { get; set; } = true;

    public Visibility BadgeVisibility =>
        string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;

    public Brush StatusBrush => Status switch
    {
        "A" => Resolve("Brush.Status.Added"),
        "M" => Resolve("Brush.Status.Modified"),
        "D" => Resolve("Brush.Status.Deleted"),
        "R" => Resolve("Brush.Status.Renamed"),
        _ => Resolve("Brush.Status.Neutral"),
    };

    private static Brush Resolve(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

public partial class TreePerformanceSpike : Window
{
    private const int TargetNodeCount = 5000;

    private readonly List<SpikeNode> _roots;
    private readonly int _nodeCount;

    public TreePerformanceSpike()
    {
        InitializeComponent();
        ThemeManager.Apply(AppTheme.System);

        (_roots, _nodeCount) = BuildSyntheticProject(TargetNodeCount);
        SubtitleText.Text =
            $"{_nodeCount:N0} nodes in a PLC-project-shaped tree (devices → applications → " +
            "folders → POUs → methods). Both panes render the same data; only the left one " +
            "virtualizes.";
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        VerdictText.Text = "measuring…";
        ResultsText.Text = string.Empty;

        // Let the button repaint before the blocking measurement starts.
        Dispatcher.BeginInvoke(new Action(Measure), DispatcherPriority.Background);
    }

    /// <summary>
    /// Run the measurement, write it to stdout and exit.
    /// </summary>
    /// <remarks>
    /// The window still has to be shown: WPF does not lay out or realise containers for an
    /// unrendered visual tree, so a genuinely headless run would measure nothing.
    /// </remarks>
    public void RunHeadless()
    {
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            Measure();
            Console.WriteLine("VERDICT  " + VerdictText.Text);
            Console.WriteLine("RESULTS  " + ResultsText.Text);
            Application.Current.Shutdown(VerdictText.Text.StartsWith("PASS", StringComparison.Ordinal)
                ? 0
                : 1);
        }), DispatcherPriority.ApplicationIdle);
    }

    private void Measure()
    {
        var virtualized = TimeBind(VirtualizedTree);
        var plain = TimeBind(PlainTree);

        var virtualizedContainers = CountContainers(VirtualizedTree);
        var plainContainers = CountContainers(PlainTree);

        // The verdict is container count, not milliseconds: it is direct evidence that
        // virtualization is on, and a machine fast enough to hide the time difference would
        // still show thousands of realised containers.
        //
        // The second clause is the anti-vacuity check. The first run of this spike passed
        // with 13 containers on BOTH trees because the tree was collapsed and nothing was
        // ever visible - a comparison that could not fail. Requiring the unvirtualized tree
        // to realise most of the nodes proves the measurement actually stressed something.
        var measurementIsMeaningful = plainContainers > _nodeCount / 2;
        var virtualizationWorking = virtualizedContainers < _nodeCount / 4;

        ResultsText.Text = string.Join("    ",
            $"nodes {_nodeCount:N0}",
            $"virtualized bind {virtualized.TotalMilliseconds,7:F1} ms",
            $"realised {virtualizedContainers,5}",
            "|",
            $"defaults bind {plain.TotalMilliseconds,7:F1} ms",
            $"realised {plainContainers,5}");

        VerdictText.Text = (measurementIsMeaningful, virtualizationWorking) switch
        {
            (false, _) => $"INCONCLUSIVE — the unvirtualized tree only realised " +
                          $"{plainContainers} of {_nodeCount:N0} nodes, so nothing was stressed",
            (true, true) => $"PASS — {virtualizedContainers} of {_nodeCount:N0} containers " +
                            $"realised (unvirtualized: {plainContainers:N0})",
            (true, false) => $"FAIL — {virtualizedContainers:N0} containers realised; " +
                             "virtualization is not working",
        };

        VerdictText.Foreground = (Brush)FindResource(
            measurementIsMeaningful && virtualizationWorking
                ? "Brush.Status.Added"
                : "Brush.Status.Deleted");

        RunButton.IsEnabled = true;
    }

    private TimeSpan TimeBind(System.Windows.Controls.TreeView tree)
    {
        tree.ItemsSource = null;
        tree.UpdateLayout();

        var sw = Stopwatch.StartNew();
        tree.ItemsSource = _roots;
        tree.UpdateLayout();
        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>Count realised visual containers — the direct measure of virtualization.</summary>
    private static int CountContainers(DependencyObject root)
    {
        var count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is System.Windows.Controls.TreeViewItem)
            {
                count++;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        return count;
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) =>
        ThemeManager.Apply(ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>
    /// Build a tree shaped like a real CODESYS project rather than a flat list, because
    /// depth is what makes hierarchical virtualization hard.
    /// </summary>
    private static (List<SpikeNode> Roots, int Count) BuildSyntheticProject(int targetNodes)
    {
        var rng = new Random(20260811);
        var roots = new List<SpikeNode>();
        var count = 0;

        SpikeNode Node(string name, string glyph, string status = "", string badge = "")
        {
            count++;
            return new SpikeNode { Name = name, Glyph = glyph, Status = status, Badge = badge };
        }

        var deviceIndex = 0;
        while (count < targetNodes)
        {
            var device = Node($"Device_{++deviceIndex}", "▣");
            roots.Add(device);

            var plcLogic = Node("Plc Logic", "▤");
            device.Children.Add(plcLogic);

            var application = Node("Application", "▦");
            plcLogic.Children.Add(application);

            foreach (var folderName in new[] { "POUs", "DUTs", "GVLs", "Visu" })
            {
                var changedInFolder = 0;
                var folder = Node(folderName, "📁");
                application.Children.Add(folder);

                for (var p = 0; p < 24 && count < targetNodes; p++)
                {
                    var status = rng.Next(10) == 0 ? "M" : string.Empty;
                    if (status.Length > 0)
                    {
                        changedInFolder++;
                    }

                    var pou = Node($"{folderName}_Block_{p:D2}", "▣", status);
                    folder.Children.Add(pou);

                    for (var m = 0; m < 3 && count < targetNodes; m++)
                    {
                        pou.Children.Add(Node($"{pou.Name}.Method_{m}", "ƒ",
                            rng.Next(20) == 0 ? "M" : string.Empty));
                    }
                }

                if (changedInFolder > 0)
                {
                    folder.Children.Insert(0, Node($"({changedInFolder} changed)", "●", "M"));
                }
            }
        }

        return (roots, count);
    }
}
