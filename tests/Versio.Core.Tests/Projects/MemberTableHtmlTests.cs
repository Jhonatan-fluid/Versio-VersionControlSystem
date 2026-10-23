using System.Text.RegularExpressions;
using Versio.Core.Diff;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The member table as HTML: the desktop's columns and their rules, old then new in the cells
/// that moved, every row findable by its path, and nothing from the file as markup.
/// </summary>
public sealed class MemberTableHtmlTests
{
    private static MemberRow Row(string name, string type = "INT", string value = "", string comment = "", int depth = 0) =>
        new() { Name = name, Path = name, Datatype = type, StartValue = value, Comment = comment, Depth = depth };

    private static MemberTable Table(params MemberRow[] rows) => new() { Name = "ST_Valve", IsDataType = true, Members = rows };

    [Fact]
    public void The_columns_are_the_desktops_and_empty_ones_are_left_out()
    {
        var html = MemberTableHtml.Fragment(Table(Row("Open", "BOOL", "FALSE", "is open")));

        Assert.Contains("<th>NAME</th><th>TYPE</th><th>DEFAULT VALUE</th><th>COMMENT</th>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTERNAL ACCESS", html, StringComparison.Ordinal);
        Assert.Contains("data-kind=\"datatype\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void External_access_appears_when_a_row_has_it()
    {
        var html = MemberTableHtml.Fragment(Table(Row("Open") with { ExternalAccess = "Read" }));

        Assert.Contains("<th>EXTERNAL ACCESS</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"mt-access\">Read</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_modified_cell_shows_old_then_new_and_only_that_cell()
    {
        var compared = MemberTableDiff.Compare(Table(Row("Speed", value: "10")), Table(Row("Speed", value: "20")));

        var html = MemberTableHtml.Fragment(compared);

        Assert.Contains("<tr class=\"mt-row mt-modified\" data-path=\"Speed\" data-change=\"modified\"", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"mt-value\"><del class=\"mt-was\">10</del> <ins class=\"mt-now\">20</ins></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"mt-type\">INT</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"mt-mark\">M</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_row_stays_and_an_added_one_is_marked()
    {
        var compared = MemberTableDiff.Compare(Table(Row("Old"), Row("Keep")), Table(Row("Keep"), Row("New")));

        var html = MemberTableHtml.Fragment(compared);

        Assert.Contains("data-path=\"Old\" data-change=\"removed\"", html, StringComparison.Ordinal);
        Assert.Contains("data-path=\"New\" data-change=\"added\"", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"mt-summary\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_is_drawn_with_spans_never_inline_styles()
    {
        var html = MemberTableHtml.Fragment(Table(Row("Outer", "ST_X"), Row("Inner", depth: 2) with { Path = "Outer.Inner" }));

        Assert.Contains("<td class=\"mt-name\"><span class=\"mt-indent\"></span><span class=\"mt-indent\"></span>Inner</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_headings_span_the_table()
    {
        var table = new MemberTable
        {
            Name = "FB_Pump",
            IsInterface = true,
            Members = [new MemberRow { Name = "Input", Path = "Input", IsSectionHeading = true }, Row("Start", "BOOL")],
        };

        var html = MemberTableHtml.Fragment(table);

        Assert.Contains("<tr class=\"mt-section\"><td colspan=\"5\">Input</td></tr>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_linked_type_carries_the_object_it_resolved_to()
    {
        var html = MemberTableHtml.Fragment(Table(Row("Cfg", "ST_Config") with { TypeReference = "ST_Config", LinkGuid = "abc-123" }));

        Assert.Contains("<a class=\"mt-link\" data-link=\"abc-123\">ST_Config</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_and_hidden_characters_from_the_file_are_neither_markup_nor_hidden()
    {
        var name = "Ok" + (char)0x202E;
        var html = MemberTableHtml.Fragment(Table(
            Row(name, comment: "<script>x</script>") with { Path = name, Was = "was" + (char)0x200B }));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0x202E).ToString(), html, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0x200B).ToString(), html, StringComparison.Ordinal);
        Assert.Contains("data-path=\"OkU+202E\"", html, StringComparison.Ordinal);
        Assert.Contains("data-hidden-chars=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"cd-notice\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_class_the_fragment_uses_is_in_the_stylesheet()
    {
        var compared = MemberTableDiff.Compare(
            Table(Row("Old"), Row("Speed", value: "10")),
            Table(Row("Speed", value: "20") with { ExternalAccess = "Read" }, Row("New", depth: 1) with { LinkGuid = "g", ReferenceChanged = true },
                new MemberRow { Name = "S", Path = "S", IsSectionHeading = true }, Row("X" + (char)0x202E)) with { Number = "5", InstanceOf = "FB" });

        var html = MemberTableHtml.Fragment(compared);

        var used = Regex.Matches(html, "class=\"([^\"]+)\"").SelectMany(m => m.Groups[1].Value.Split(' ')).Distinct().ToList();
        Assert.Contains("mt-ref-changed", used);
        Assert.All(used, c => Assert.Matches("\\." + Regex.Escape(c) + "[\\s,{:>.\\[)]", CodePageAssets.Css));
    }

    [Fact]
    public void Attributes_are_chips_over_the_table_and_under_a_name_and_an_unshown_change_is_said()
    {
        var before = Table(Row("Scada", "ST_AlarmScada") with { Attributes = [new() { Name = "symbol", Value = "read" }] });
        var after = Table(Row("Scada", "ST_AlarmScada") with { Attributes = [new() { Name = "symbol", Value = "readwrite" }] })
            with { Attributes = [new() { Name = "linkalways" }], Unshown = [new UnshownLine(1, "/// new doc")] };

        var html = MemberTableHtml.Fragment(MemberTableDiff.Compare(before, after), compared: true);

        Assert.Contains("<div class=\"mt-attrs\"><span class=\"mt-attrs-label\">Attributes</span><span class=\"mt-chip mt-chip-added\" title=\"Added since the last commit\">linkalways</span></div>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"mt-chips\"><span class=\"mt-chip mt-chip-modified\" title=\"Changed since the last commit: was read\">symbol = readwrite <del class=\"mt-was\">read</del></span></span>", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"mt-unshown\" role=\"note\">1 declaration change is not shown in this table (line 1)", html, StringComparison.Ordinal);
    }
}
