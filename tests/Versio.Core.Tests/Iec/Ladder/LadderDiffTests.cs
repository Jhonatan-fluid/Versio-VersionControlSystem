using Versio.Core.Iec.Ladder;
using Xunit;
using static Versio.Core.Tests.Iec.Ladder.LadderTestNetworks;

namespace Versio.Core.Tests.Iec.Ladder;

/// <summary>
/// Networks are matched by content, never by number, and a rung deleted with another written in
/// its place is a removal and an addition, never an edit.
/// </summary>
public sealed class LadderDiffTests
{
    /// <summary>A three-element rung: two contacts and a coil, ids prefixed so each side differs.</summary>
    private static LadderNetwork Rung(int number, string prefix, string a, string b, string coil,
        LdContactKind second = LdContactKind.NormallyOpen, string title = "") =>
        FromTree(
            S(L(No(prefix + "a", a)), L(new LdContact(prefix + "b", b, second)), L(Coil(prefix + "c", coil))),
            number,
            title);

    private static LadderBody Block(params LadderNetwork[] networks) =>
        new("Main", networks.Select((n, i) => n with { Number = i + 1 }).ToList());

    private static LadderNetwork A(string p = "") => Rung(0, p + "A", "Start", "Stop", "Motor");

    private static LadderNetwork B(string p = "") => Rung(0, p + "B", "Level_Hi", "Pump_Ok", "Valve");

    private static LadderNetwork C(string p = "") => Rung(0, p + "C", "Door", "Guard", "Alarm");

    private static LadderNetwork D(string p = "") => Rung(0, p + "D", "Heat_Req", "Temp_Ok", "Heater");

    private static IEnumerable<LadderChange> Changes(LadderComparison c) => c.Rows.Select(r => r.Change);

    [Fact]
    public void An_unchanged_block_is_all_same_even_when_the_vendor_renumbered_its_ids()
    {
        var result = LadderDiff.Compare(Block(A("x"), B("x")), Block(A("y"), B("y")));

        Assert.Equal([LadderChange.Same, LadderChange.Same], Changes(result));
        Assert.False(result.HasChanges);
    }

    [Fact]
    public void One_operand_renamed_is_that_network_changed_and_only_that_contact_modified()
    {
        var old = Block(A(), Rung(0, "o", "Level_Hi", "Pump_Ok", "Valve"));
        var @new = Block(A(), Rung(0, "n", "Level_Hi", "Pump_Ready", "Valve"));

        var row = LadderDiff.Compare(old, @new).Rows[1];

        Assert.Equal(LadderChange.Changed, row.Change);
        Assert.Equal(LdMark.Same, row.New!.Marks["na"]);
        Assert.Equal(LdMark.Modified, row.New.Marks["nb"]);
        Assert.Equal(LdMark.Modified, row.Old!.Marks["ob"]);
        Assert.Equal(LdMark.Same, row.New.Marks["nc"]);
    }

    [Fact]
    public void A_contact_turned_from_no_to_nc_is_modified()
    {
        var old = Block(Rung(0, "o", "Start", "Stop", "Motor"));
        var @new = Block(Rung(0, "n", "Start", "Stop", "Motor", LdContactKind.NormallyClosed));

        var row = Assert.Single(LadderDiff.Compare(old, @new).Rows);

        Assert.Equal(LadderChange.Changed, row.Change);
        Assert.Equal(LdMark.Modified, row.New!.Marks["nb"]);
    }

    [Fact]
    public void A_network_added_in_the_middle_is_added_and_its_neighbours_are_unchanged()
    {
        var result = LadderDiff.Compare(Block(A(), B()), Block(A(), C(), B()));

        Assert.Equal([LadderChange.Same, LadderChange.Added, LadderChange.Same], Changes(result));
        Assert.Null(result.Rows[1].Old);
        Assert.All(result.Rows[1].New!.Marks.Values, m => Assert.Equal(LdMark.Added, m));
    }

    [Fact]
    public void A_network_removed_shifts_the_numbers_after_it_without_marking_them()
    {
        var result = LadderDiff.Compare(Block(A(), C(), B()), Block(A(), B()));

        Assert.Equal([LadderChange.Same, LadderChange.Removed, LadderChange.Same], Changes(result));
        Assert.Equal(3, result.Rows[2].Old!.Network.Number);
        Assert.Equal(2, result.Rows[2].New!.Network.Number);
    }

    [Fact]
    public void A_rung_deleted_and_an_unrelated_one_in_its_place_is_replaced_never_changed()
    {
        // The owner's case: same position, same shape (two contacts and a coil), nothing else
        // in common. One row, the old rung marked removed and the new one added.
        var result = LadderDiff.Compare(Block(A(), B(), C()), Block(A(), D(), C()));

        Assert.Equal([LadderChange.Same, LadderChange.Replaced, LadderChange.Same], Changes(result));
        var row = result.Rows[1];
        Assert.All(row.Old!.Marks.Values, m => Assert.Equal(LdMark.Removed, m));
        Assert.All(row.New!.Marks.Values, m => Assert.Equal(LdMark.Added, m));
        Assert.Equal(0, result.ChangedCount);
    }

    [Fact]
    public void Two_networks_swapped_is_one_moved_and_nothing_added_or_removed()
    {
        // Swapping two neighbours moves one of them past the other; the other kept its order.
        var result = LadderDiff.Compare(Block(A(), B(), C()), Block(A(), C(), B()));

        Assert.Equal(1, result.MovedCount);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.RemovedCount);
        var stub = Assert.Single(result.Rows, r => r.Change == LadderChange.MovedAway);
        var moved = Assert.Single(result.Rows, r => r.Change == LadderChange.Moved);
        Assert.Equal(moved.New!.Network.Number, stub.MovedTo);
    }

    [Fact]
    public void A_network_moved_and_edited_is_drawn_where_it_is_now_with_a_stub_where_it_was()
    {
        var edited = Rung(0, "e", "Heat_Req", "Temp_Ok", "Heater_On");
        var result = LadderDiff.Compare(Block(A(), B(), C(), D()), Block(edited, A(), B(), C()));

        Assert.Equal(
            [LadderChange.MovedAndChanged, LadderChange.Same, LadderChange.Same, LadderChange.Same, LadderChange.MovedAway],
            Changes(result));
        Assert.Equal(1, result.Rows[^1].MovedTo);
        Assert.Equal(LdMark.Modified, result.Rows[0].New!.Marks["ec"]);
    }

    [Fact]
    public void A_title_only_change_is_changed_with_the_header_marked_and_no_element_marked()
    {
        var old = Block(Rung(0, "o", "Start", "Stop", "Motor", title: "Motor"));
        var @new = Block(Rung(0, "n", "Start", "Stop", "Motor", title: "Main motor"));

        var row = Assert.Single(LadderDiff.Compare(old, @new).Rows);

        Assert.Equal(LadderChange.Changed, row.Change);
        Assert.True(row.HeaderChanged);
        Assert.All(row.New!.Marks.Values, m => Assert.Equal(LdMark.Same, m));
    }

    [Fact]
    public void The_threshold_holds_on_both_sides()
    {
        LadderNetwork Four(string p, string a, string b, string c, string d) => FromTree(
            S(L(No(p + "1", a)), L(No(p + "2", b)), L(No(p + "3", c)), L(Coil(p + "4", d))), 1);

        var original = Four("o", "W", "X", "Y", "Z");
        var oneOfFour = Four("n", "W", "X", "Y", "Q");     // exact 3/5, shape 1, operands 3/5: 0.73
        var twoOfFour = Four("n", "W", "X", "P", "Q");     // exact 2/6, shape 1, operands 2/6: 0.56

        Assert.True(LadderDiff.Similarity(original, oneOfFour) >= LadderDiff.SimilarityThreshold);
        Assert.True(LadderDiff.Similarity(original, twoOfFour) < LadderDiff.SimilarityThreshold);
        Assert.Equal(LadderChange.Changed, LadderDiff.Compare(Block(original), Block(oneOfFour)).Rows[0].Change);
        Assert.Equal(LadderChange.Replaced, LadderDiff.Compare(Block(original), Block(twoOfFour)).Rows[0].Change);
    }

    [Fact]
    public void A_block_with_nothing_to_compare_with_is_all_added()
    {
        var result = LadderDiff.Compare(null, Block(A(), B()));

        Assert.False(result.HasOldSide);
        Assert.Equal([LadderChange.Added, LadderChange.Added], Changes(result));
    }

    [Fact]
    public void A_contact_inserted_at_the_start_marks_only_that_contact()
    {
        // Matching by tree position would shift every element after the insert and mark the
        // whole rung modified.
        var old = Block(Rung(0, "o", "Start", "Stop", "Motor"));
        var @new = Block(FromTree(S(
            L(No("nx", "Enable")), L(No("na", "Start")), L(No("nb", "Stop")), L(Coil("nc", "Motor")))));

        var row = Assert.Single(LadderDiff.Compare(old, @new).Rows);

        Assert.Equal(LadderChange.Changed, row.Change);
        Assert.Equal(LdMark.Added, row.New!.Marks["nx"]);
        Assert.Equal([LdMark.Same, LdMark.Same, LdMark.Same], new[] { "na", "nb", "nc" }.Select(k => row.New.Marks[k]));
    }
}
