using System.Text.RegularExpressions;
using Versio.Core.Diff;
using Versio.Core.Projects;
using Versio.Core.Review;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The three catalog tables as HTML: the desktop's columns, old then new in the cells that moved,
/// every row findable by its key and its anchors, and nothing from the file as markup.
/// </summary>
public sealed class CatalogHtmlTests
{
    private static LibraryReference Lib(string name, string resolution, string ns = "") =>
        new() { Name = name, Namespace = ns.Length > 0 ? ns : name, DefaultResolution = resolution, SystemLibrary = true };

    private static LibraryCatalog Libraries(params LibraryReference[] references) =>
        new() { Schema = 1, Manager = "Library Manager", Path = "Device/Library Manager", References = references };

    private static TaskRecord Task(string name, string priority = "1", string interval = "t#20ms", params string[] pous) =>
        new() { Name = name, Priority = priority, Interval = interval, IntervalUnit = "ms", Pous = pous };

    private static TaskCatalog Tasks(params TaskRecord[] tasks) =>
        new() { Schema = 1, Configuration = "Task Configuration", Path = "Device/Task Configuration", Tasks = tasks };

    private static DeviceCatalog Device(params DeviceConnector[] connectors) =>
        new()
        {
            Schema = 1,
            Name = "EtherCAT_Master",
            Path = "Device/EtherCAT_Master",
            Connectors = connectors,
            Slots = new Dictionary<string, string>(StringComparer.Ordinal) { ["0"] = "<Empty>", ["1"] = "<Empty>" },
        };

    [Fact]
    public void The_library_table_has_the_desktops_columns_and_caption()
    {
        var html = CatalogHtml.Fragment(Libraries(Lib("Standard", "3.5.17.0")));

        Assert.StartsWith("<div class=\"ct-page\" data-kind=\"libraries\" data-src-path=\"Device/Library Manager\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ct-title\">Library Manager</span>", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ct-note\">" + CatalogHtml.LibraryNote + "</p>", html, StringComparison.Ordinal);
        Assert.Contains("<th class=\"ct-mark\"></th><th>NAME</th><th>RESOLUTION</th><th>NAMESPACE</th><th>KIND</th>", html, StringComparison.Ordinal);
        Assert.Contains("data-key=\"Standard\"", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-cell\">system</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_task_and_device_tables_have_theirs_and_the_device_lists_its_empty_slots()
    {
        var tasks = CatalogHtml.Fragment(Tasks(Task("MainTask", pous: ["PLC_PRG", "Simulation"])));
        var device = CatalogHtml.Fragment(Device(new DeviceConnector { ConnectorId = "1", Interface = "Common.Ethercat", Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "1" } }));

        Assert.Contains("data-kind=\"tasks\"", tasks, StringComparison.Ordinal);
        Assert.Contains("<th>TASK</th><th>INTERVAL</th><th>PRIO</th><th>WATCHDOG</th><th>CALLS</th>", tasks, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-cell\">PLC_PRG, Simulation</td>", tasks, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-cell\">off</td>", tasks, StringComparison.Ordinal);
        Assert.Contains(CatalogHtml.TaskNote, tasks, StringComparison.Ordinal);

        Assert.Contains("data-kind=\"devices\"", device, StringComparison.Ordinal);
        Assert.Contains("<th>CONNECTOR</th><th>ID</th><th>PARAMETERS</th>", device, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-name\">Common.Ethercat</td><td class=\"ct-cell\">1</td><td class=\"ct-cell\">1 param(s)</td>", device, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"ct-section\"><td colspan=\"4\">Empty slots</td></tr>", device, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"ct-row ct-slot\" data-key=\"0\"><td class=\"ct-mark\"></td><td class=\"ct-name\">0</td><td class=\"ct-cell\" colspan=\"2\">&lt;Empty&gt;</td></tr>", device, StringComparison.Ordinal);
        Assert.Contains(CatalogHtml.DeviceNote, device, StringComparison.Ordinal);
    }

    [Fact]
    public void A_modified_row_shows_old_then_new_in_the_cell_that_moved_and_is_marked()
    {
        var compared = CatalogDiff.Compare(Libraries(Lib("Standard", "3.5.17.0")), Libraries(Lib("Standard", "3.5.19.0")));

        var html = CatalogHtml.Fragment(compared);

        Assert.Contains("<tr class=\"ct-row ct-modified\" data-key=\"Standard\" data-change=\"modified\"", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-mark\">M</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-cell\"><del class=\"ct-was\">3.5.17.0</del> <ins class=\"ct-now\">3.5.19.0</ins></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-cell\">Standard</td>", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ct-summary\">", html, StringComparison.Ordinal);
        Assert.Contains(" title=\"Was: ", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_row_stays_struck_and_an_added_one_is_marked()
    {
        var compared = CatalogDiff.Compare(Tasks(Task("Old"), Task("Keep")), Tasks(Task("Keep"), Task("New")));

        var html = CatalogHtml.Fragment(compared);

        Assert.Contains("ct-removed\" data-key=\"Old\" data-change=\"removed\"", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-mark\">D</td>", html, StringComparison.Ordinal);
        Assert.Contains("ct-added\" data-key=\"New\" data-change=\"added\"", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"ct-mark\">A</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Anchors_and_markup_name_the_same_rows_on_each_side()
    {
        var compared = CatalogDiff.Compare(Tasks(Task("Old"), Task("Keep", "1")), Tasks(Task("Keep", "2"), Task("New")));

        var html = CatalogHtml.Fragment(compared, compared: true, sourcePath: "p/Task Configuration.tasks.yaml");
        var anchors = CatalogHtml.Anchors(compared, compared: true, sourcePath: "p/Task Configuration.tasks.yaml");

        // Every anchor id the list names is on exactly one row, and every id on a row is in the list.
        var inMarkup = Regex.Matches(html, "data-anchors=\"([^\"]*)\"").SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        Assert.Equal(inMarkup.Order(StringComparer.Ordinal), anchors.Select(a => a.Dom).Order(StringComparer.Ordinal));
        Assert.Equal(inMarkup.Count, inMarkup.Distinct().Count());

        // A kept row is on both sides, a removed one on the old only, an added one on the new only.
        Assert.Contains(anchors, a => a.Key == "Keep" && a.Side == ReviewSide.Old);
        Assert.Contains(anchors, a => a.Key == "Keep" && a.Side == ReviewSide.New);
        Assert.Equal(ReviewSide.Old, Assert.Single(anchors, a => a.Key == "Old").Side);
        Assert.Equal(ReviewSide.New, Assert.Single(anchors, a => a.Key == "New").Side);
        Assert.All(anchors, a => Assert.Equal("p/Task Configuration.tasks.yaml", a.SourcePath));
        Assert.All(anchors, a => Assert.Equal("row", a.Kind));
        Assert.All(anchors, a => Assert.Equal("Task Configuration", a.Container));

        // The two sides of the modified row fingerprint what each side said.
        var keep = anchors.Where(a => a.Key == "Keep").ToList();
        Assert.NotEqual(keep[0].Fingerprint, keep[1].Fingerprint);

        // A single version has no older side.
        Assert.All(CatalogHtml.Anchors(compared, compared: false), a => Assert.Equal(ReviewSide.New, a.Side));
        Assert.Contains("data-src-path=\"p/Task Configuration.tasks.yaml\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_rows_are_keyed_as_the_tree_keys_them_so_duplicates_stay_apart()
    {
        var a = new DeviceConnector { Interface = "Common.Ethercat", Parameters = new Dictionary<string, string>(StringComparer.Ordinal) };
        var html = CatalogHtml.Fragment(Device(a, a));
        var anchors = CatalogHtml.Anchors(Device(a, a), compared: false);

        Assert.Equal(2, Regex.Matches(html, "data-key=\"Common.Ethercat[^\"]*\"").Count);
        Assert.Equal(2, anchors.Select(x => x.Key).Distinct().Count());
    }

    [Fact]
    public void Markup_and_hidden_characters_from_the_file_are_neither_markup_nor_hidden()
    {
        var name = "Std" + (char)0x202E;
        var html = CatalogHtml.Fragment(Libraries(Lib(name, "<script>x</script>", ns: "Std")));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0x202E).ToString(), html, StringComparison.Ordinal);
        Assert.Contains("data-key=\"StdU+202E\"", html, StringComparison.Ordinal);
        Assert.Contains("data-hidden-chars=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"cd-notice\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_class_the_fragments_use_is_in_the_stylesheet()
    {
        var libraries = CatalogHtml.Fragment(CatalogDiff.Compare(Libraries(Lib("Old", "1"), Lib("S", "1")), Libraries(Lib("S", "2"), Lib("New", "1"))));
        var tasks = CatalogHtml.Fragment(CatalogDiff.Compare(Tasks(Task("A")), Tasks(Task("A", "2"))));
        var device = CatalogHtml.Fragment(Device(new DeviceConnector { ConnectorId = "1", Parameters = new Dictionary<string, string>(StringComparer.Ordinal) }));

        var used = new[] { libraries, tasks, device }
            .SelectMany(html => Regex.Matches(html, "class=\"([^\"]+)\"").SelectMany(m => m.Groups[1].Value.Split(' ')))
            .Distinct()
            .ToList();
        Assert.Contains("ct-removed", used);
        Assert.Contains("ct-slot", used);
        Assert.All(used, c => Assert.Matches("\\." + Regex.Escape(c) + "[\\s,{:>.\\[)]", CodePageAssets.Css));
    }
}
