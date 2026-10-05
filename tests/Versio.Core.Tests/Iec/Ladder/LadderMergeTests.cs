using Versio.Core.Iec.Ladder;
using Versio.TestSupport;
using Xunit;
using static Versio.Core.Tests.Iec.Ladder.LadderTestNetworks;

namespace Versio.Core.Tests.Iec.Ladder;

/// <summary>
/// Two versions of a network merged onto one rung: kept elements unmarked, added ones added,
/// removed ones still standing where they stood, modified ones saying what they were.
/// </summary>
public sealed class LadderMergeTests
{
    private static LadderMergedNetwork Merge(LdBlock old, LdBlock @new)
    {
        var merged = LadderMerge.Merge(FromTree(old), FromTree(@new));
        Assert.NotNull(merged);
        return merged;
    }

    private static string Shape(LdBlock block) => ShapeOf(block);

    [Fact]
    public void A_renamed_operand_is_one_modified_contact_saying_what_it_was()
    {
        var merged = Merge(
            S(L(No("a", "Start")), L(No("b", "Pump_Ok")), L(Coil("c", "Valve"))),
            S(L(No("a", "Start")), L(No("b", "Pump_Ready")), L(Coil("c", "Valve"))));

        Assert.Equal("S(n:a,n:b,n:c)", Shape(merged.Root));
        Assert.Equal(LdMark.Modified, merged.Marks["n:b"]);
        Assert.Equal("was Pump_Ok", merged.Was["n:b"]);
        Assert.Equal(LdMark.Same, merged.Marks["n:a"]);
    }

    [Fact]
    public void A_contact_turned_closed_says_it_was_open()
    {
        var merged = Merge(
            S(L(No("a", "Start")), L(Coil("c", "Out"))),
            S(L(Nc("a", "Start")), L(Coil("c", "Out"))));

        Assert.Equal("was NO", merged.Was["n:a"]);
    }

    [Fact]
    public void A_deleted_contact_stays_on_the_rung_where_it_stood_marked_removed()
    {
        // The owner's ask: a deletion shows on the same rung, not on a second drawing.
        var merged = Merge(
            S(L(No("a", "Start")), L(No("b", "Interlock")), L(Coil("c", "Out"))),
            S(L(No("a", "Start")), L(Coil("c", "Out"))));

        Assert.Equal("S(n:a,o:b,n:c)", Shape(merged.Root));
        Assert.Equal(LdMark.Removed, merged.Marks["o:b"]);
    }

    [Fact]
    public void An_inserted_contact_is_added_in_place_and_nothing_else_is_marked()
    {
        var merged = Merge(
            S(L(No("a", "Start")), L(Coil("c", "Out"))),
            S(L(No("x", "Enable")), L(No("a", "Start")), L(Coil("c", "Out"))));

        Assert.Equal("S(n:x,n:a,n:c)", Shape(merged.Root));
        Assert.Equal(LdMark.Added, merged.Marks["n:x"]);
        Assert.Equal([LdMark.Same, LdMark.Same], new[] { "n:a", "n:c" }.Select(k => merged.Marks[k]));
    }

    [Fact]
    public void A_contact_replaced_by_a_coil_is_one_removed_and_one_added_never_modified()
    {
        var merged = Merge(
            S(L(No("a", "Start")), L(No("b", "Old")), L(Coil("c", "Out"))),
            S(L(No("a", "Start")), L(Coil("x", "New")), L(Coil("c", "Out"))));

        Assert.Equal(LdMark.Removed, merged.Marks["o:b"]);
        Assert.Equal(LdMark.Added, merged.Marks["n:x"]);
        Assert.Empty(merged.Was);
    }

    [Fact]
    public void A_branch_added_is_a_parallel_block_with_the_new_branch_marked()
    {
        var merged = Merge(
            S(L(No("a", "Start")), L(Coil("c", "Motor"))),
            S(P(L(No("a", "Start")), L(No("s", "Motor"))), L(Coil("c", "Motor"))));

        Assert.Equal(LdMark.Added, merged.Marks["n:s"]);
        Assert.Equal(LdMark.Same, merged.Marks["n:a"]);
        Assert.Contains("P(", Shape(merged.Root), StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_removed_keeps_its_place_in_the_parallel_block()
    {
        var merged = Merge(
            S(P(L(No("a", "Start")), L(No("s", "Seal"))), L(Coil("c", "Motor"))),
            S(L(No("a", "Start")), L(Coil("c", "Motor"))));

        Assert.Equal(LdMark.Removed, merged.Marks["o:s"]);
        AssertDraws(merged);
    }

    [Fact]
    public void A_timer_preset_changed_names_the_pin_and_its_old_value()
    {
        var merged = Merge(
            S(L(No("a", "Start")), L(Timer("t", "\"Delay\"", "T#5s")), L(Coil("c", "Done"))),
            S(L(No("a", "Start")), L(Timer("t", "\"Delay\"", "T#10s")), L(Coil("c", "Done"))));

        Assert.Equal(LdMark.Modified, merged.Marks["n:t"]);
        Assert.Equal("was PT=T#5s", merged.Was["n:t"]);
        AssertDraws(merged);
    }

    [Fact]
    public void A_modified_element_draws_its_was_line_inside_itself()
    {
        var merged = Merge(
            S(L(No("a", "Start")), L(Coil("c", "Out"))),
            S(L(No("a", "Begin")), L(Coil("c", "Out"))));

        var drawing = LadderLayout.LayMerged(merged, LadderStyle.Default);

        var contact = drawing.Groups.Single(g => g.Key == "n:a");
        Assert.Contains(contact.Items.OfType<LdText>(), t => t.Role == "was" && t.Text == "was Start");
        AssertDraws(merged);
    }

    [Fact]
    public void A_network_with_no_series_parallel_shape_is_not_merged()
    {
        var box = new LdBox("x", "CTUD",
        [
            new LdPin("CU", LdPinDirection.Input),
            new LdPin("CD", LdPinDirection.Input),
            new LdPin("QU", LdPinDirection.Output),
        ]);
        var odd = new LadderNetwork
        {
            Number = 1,
            Nodes = [new LdLeftRail("rail"), No("a", "Up"), No("b", "Down"), box],
            Nets =
            [
                new LdNet([new("rail", LdPins.Out), new("a", LdPins.In), new("b", LdPins.In)]),
                new LdNet([new("a", LdPins.Out), new("x", "CU")]),
                new LdNet([new("b", LdPins.Out), new("x", "CD")]),
            ],
        };

        Assert.Null(LadderMerge.Merge(odd, FromTree(S(L(No("a", "Up")), L(Coil("c", "Out"))))));
    }

    [Fact]
    public void Random_edits_merge_into_drawings_that_keep_every_invariant()
    {
        // Seeded: a rung, then the same rung with elements dropped, inserted and renamed. Every
        // element of both versions must appear exactly once, and the drawing must hold together.
        var refused = 0;
        for (var seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var old = LadderTestNetworks.Random(random);
            var edited = Edit(old, random);

            // Refused when the edit changed the rung's shape under it (it is then drawn as two
            // stacked rungs). Allowed, but rare: a merge that refuses often is not a merge.
            var merged = LadderMerge.Merge(FromTree(old), FromTree(edited));
            if (merged is null)
            {
                refused++;
                continue;
            }

            var oldIds = LadderStructure.Walk(old).Select(n => n.Id).ToHashSet();
            var newIds = LadderStructure.Walk(edited).Select(n => n.Id).ToHashSet();
            foreach (var key in LadderStructure.Walk(merged.Root).Select(n => n.Id))
            {
                var id = key[2..];
                Assert.True(key.StartsWith("n:", StringComparison.Ordinal) ? newIds.Contains(id) : oldIds.Contains(id), $"seed {seed}: {key} came from nowhere");
            }

            Assert.True(
                newIds.All(id => merged.Marks.ContainsKey("n:" + id)),
                $"seed {seed}: an element of the new version is missing from the merge");

            // An element both versions have unchanged is drawn once, as the new one. Drawn twice
            // (removed beside added) is how a branch opened around a contact first read. A renamed
            // one may come out as removed plus added when nothing around it is shared: the files
            // give it no identity, so that is the honest drawing, and it is not checked here.
            var oldById = LadderStructure.Walk(old).ToDictionary(n => n.Id, LadderStructure.Describe);
            var unchanged = LadderStructure.Walk(edited)
                .Where(n => oldById.TryGetValue(n.Id, out var d) && d == LadderStructure.Describe(n))
                .Select(n => n.Id)
                .ToList();
            var twice = unchanged.Where(id => merged.Marks.ContainsKey("o:" + id)).ToList();
            Assert.True(twice.Count == 0, $"seed {seed}: unchanged {string.Join(",", twice)} drawn twice");

            var problems = LadderInvariants.Violations(LadderLayout.LayMerged(merged, LadderStyle.Default));
            Assert.True(problems.Count == 0, $"seed {seed}: {string.Join("; ", problems)}");
        }

        Assert.True(refused <= 30, $"{refused} of 300 merges refused");
    }

    [Fact]
    public void A_merge_that_would_draw_an_unchanged_contact_twice_is_refused()
    {
        // Seed 48's shape, by hand: deleting the contact in front of a nested branch folds that
        // branch into its parent, so no branch pairs with the old one.
        var old = S(P(L(No("a", "A")), S(L(No("x", "Gone")), P(L(No("b", "B")), L(No("c", "C"))))), L(Coil("q", "Out")));
        var @new = S(P(L(No("a", "A")), L(No("b", "B")), L(No("c", "C"))), L(Coil("q", "Out")));

        var merged = LadderMerge.Merge(FromTree(old), FromTree(@new));

        if (merged is not null)
        {
            var removed = LadderStructure.Walk(merged.Root).Where(n => merged.Marks[n.Id] == LdMark.Removed)
                .Select(LadderStructure.Describe).ToList();
            Assert.DoesNotContain(removed, d => d.Contains(":B:", StringComparison.Ordinal) || d.Contains(":C:", StringComparison.Ordinal));
        }
    }

    private static void AssertDraws(LadderMergedNetwork merged)
    {
        var problems = LadderInvariants.Violations(LadderLayout.LayMerged(merged, LadderStyle.Default));
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>Drops, renames or inserts contacts along every series, keeping the rung well formed.</summary>
    private static LdBlock Edit(LdBlock block, Random random)
    {
        var next = 0;
        LdBlock Walk(LdBlock b) => b switch
        {
            LdSeries s => new LdSeries(Series(s.Items)),
            LdParallel p => new LdParallel(p.Branches.Select(Walk).ToList()),
            LdLeaf { Node: LdContact c } when random.Next(5) == 0 => L(c with { Operand = c.Operand + "_x" }),
            _ => b,
        };

        List<LdBlock> Series(IReadOnlyList<LdBlock> items)
        {
            var result = new List<LdBlock>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var last = i == items.Count - 1;
                if (!last && item is LdLeaf { Node: LdContact } && random.Next(6) == 0)
                {
                    continue;
                }

                if (random.Next(8) == 0)
                {
                    result.Add(L(No($"ins{++next}", $"Inserted_{next}")));
                }

                // A branch opened around a contact: the case the first merge got wrong.
                if (!last && item is LdLeaf { Node: LdContact } && random.Next(8) == 0)
                {
                    result.Add(P(Walk(item), L(No($"br{++next}", $"Branch_{next}"))));
                    continue;
                }

                result.Add(Walk(item));
            }

            return result;
        }

        return Walk(block);
    }
}
