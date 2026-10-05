using Versio.Core.Iec.Ladder;
using Xunit;

namespace Versio.Core.Tests.Iec.Ladder;

/// <summary>
/// A changed function block diagram drawn once when nothing was removed, its modified boxes
/// saying what they were; stacked only when an element is gone.
/// </summary>
/// <remarks>
/// The owner, 2026-10-05, on the first FBD cut: a renamed title and one changed operand each drew
/// the whole network twice.
/// </remarks>
public sealed class FunctionBlockMergeTests
{
    /// <summary>An AND of two operands into an assignment; ids fixed so both versions pair.</summary>
    private static LadderNetwork Gate(string a, string b, string target, string title = "", bool third = false)
    {
        var pins = new List<LdPin> { new("in1", LdPinDirection.Input, a), new("in2", LdPinDirection.Input, b) };
        if (third)
        {
            pins.Add(new LdPin("in3", LdPinDirection.Input, "\"Extra\""));
        }

        pins.Add(new LdPin("out", LdPinDirection.Output));
        return new LadderNetwork
        {
            Number = 1,
            Title = title,
            Language = LdLanguage.FunctionBlock,
            Nodes = [new LdBox("g", "&", pins), new LdCoil("q", target, LdCoilKind.Normal)],
            Nets = [new LdNet([new LdPinRef("g", "out"), new LdPinRef("q", LdPins.In)])],
        };
    }

    private static string Html(LadderNetwork old, LadderNetwork @new, LadderView view = LadderView.Merged) =>
        LadderHtml.Fragment(
            LadderDiff.Compare(new LadderBody("B", [old]), new LadderBody("B", [@new])), LadderStyle.Default, view);

    private static int Svgs(string html) => html.Split("<svg ").Length - 1;

    [Fact]
    public void A_renamed_operand_is_one_diagram_with_the_box_saying_what_it_was()
    {
        var html = Html(Gate("\"Start\"", "\"Ready\"", "\"Run\""), Gate("\"Start\"", "\"Ready_Ok\"", "\"Run\""));

        Assert.Equal(1, Svgs(html));
        Assert.Contains("data-key=\"g\" data-change=\"modified\"", html, StringComparison.Ordinal);
        Assert.Contains("was in2=\"Ready\"", WebDecode(html), StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_only_change_is_one_diagram_with_the_old_title()
    {
        var html = Html(Gate("\"A\"", "\"B\"", "\"Q\"", "Flow"), Gate("\"A\"", "\"B\"", "\"Q\"", "Flow (renamed)"));

        Assert.Equal(1, Svgs(html));
        Assert.Contains("<span class=\"ld-was\">was Flow</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_assignment_retargeted_says_what_it_wrote_before()
    {
        var merged = LadderMerge.FunctionBlockWas(Gate("\"A\"", "\"B\"", "\"Out[1]\""), Gate("\"A\"", "\"B\"", "\"Out[5]\""));

        Assert.NotNull(merged);
        Assert.Equal("was \"Out[1]\"", merged["q"]);
    }

    [Fact]
    public void A_removed_box_is_drawn_as_the_two_versions_stacked()
    {
        // The old version has an element the new one lacks: there is no wire to hang it on.
        var old = Gate("\"A\"", "\"B\"", "\"Q\"") with
        {
            Nodes = [.. Gate("\"A\"", "\"B\"", "\"Q\"").Nodes, new LdCoil("q2", "\"Spare\"", LdCoilKind.Normal)],
        };

        Assert.Null(LadderMerge.FunctionBlockWas(old, Gate("\"A\"", "\"B\"", "\"Q\"")));
        Assert.Equal(2, Svgs(Html(old, Gate("\"A\"", "\"B\"", "\"Q\""))));
    }

    [Fact]
    public void Side_by_side_still_draws_both_versions()
    {
        var html = Html(Gate("\"A\"", "\"B\"", "\"Q\""), Gate("\"A\"", "\"C\"", "\"Q\""), LadderView.SideBySide);

        Assert.Equal(2, Svgs(html));
    }

    [Fact]
    public void The_was_line_stays_inside_its_box_and_keeps_every_invariant()
    {
        var was = LadderMerge.FunctionBlockWas(Gate("\"A\"", "\"B\"", "\"Q\""), Gate("\"A\"", "\"B_long_name_here\"", "\"Q\""))!;

        var drawing = LadderLayout.Lay(Gate("\"A\"", "\"B_long_name_here\"", "\"Q\""), LadderStyle.Default, was);

        Assert.Contains(drawing.Groups.Single(g => g.Key == "g").Items.OfType<LdText>(), t => t.Role == "was");
        Assert.Empty(Versio.TestSupport.LadderInvariants.Violations(drawing));
    }

    private static string WebDecode(string html) => System.Net.WebUtility.HtmlDecode(html);
}
