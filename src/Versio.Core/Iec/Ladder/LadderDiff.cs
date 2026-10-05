namespace Versio.Core.Iec.Ladder;

/// <summary>What happened to one network between the committed block and the working one.</summary>
public enum LadderChange
{
    Same,

    /// <summary>The same network, edited in place.</summary>
    Changed,

    Added,

    Removed,

    /// <summary>
    /// A network removed and an unrelated one added at the same place: drawn on one row, the
    /// old one marked removed and the new one added. Never called "changed".
    /// </summary>
    Replaced,

    /// <summary>Identical content at another position. Drawn where it is now.</summary>
    Moved,

    /// <summary>Moved and edited.</summary>
    MovedAndChanged,

    /// <summary>The thin row left where a moved network used to be.</summary>
    MovedAway,
}

/// <summary>What happened to one element inside a network.</summary>
public enum LdMark { Same, Modified, Added, Removed }

/// <summary>One side of a row: the network, and a mark for each of its elements by id.</summary>
public sealed record LadderSide(LadderNetwork Network, IReadOnlyDictionary<string, LdMark> Marks);

/// <summary>One row of the side-by-side view: the old network on the left, the new on the right.</summary>
public sealed record LadderRow(LadderChange Change, LadderSide? Old, LadderSide? New)
{
    /// <summary>The network's title or comment differs between the two sides.</summary>
    public bool HeaderChanged { get; init; }

    /// <summary>For a <see cref="LadderChange.MovedAway"/> row: the number the network has now.</summary>
    public int? MovedTo { get; init; }
}

/// <summary>A block compared network by network, in the order the rows are drawn.</summary>
/// <param name="HasOldSide">False when there was nothing to compare with, such as a block never committed.</param>
public sealed record LadderComparison(string BlockName, IReadOnlyList<LadderRow> Rows, bool HasOldSide)
{
    public int ChangedCount => Rows.Count(r => r.Change is LadderChange.Changed or LadderChange.MovedAndChanged);

    public int AddedCount => Rows.Count(r => r.Change is LadderChange.Added or LadderChange.Replaced);

    public int RemovedCount => Rows.Count(r => r.Change is LadderChange.Removed or LadderChange.Replaced);

    public int MovedCount => Rows.Count(r => r.Change is LadderChange.Moved or LadderChange.MovedAndChanged);

    public bool HasChanges => Rows.Any(r => r.Change != LadderChange.Same);
}

/// <summary>
/// Compares two versions of a block network by network, by content, never by number.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why never by number.</b> Every vendor seen so far numbers networks by position. Delete
/// network 2 and the old network 3 is now network 2, so comparing "network 3 with network 3"
/// would mark every network after a delete as rewritten. Networks are matched by a fingerprint
/// of what they draw (<see cref="LadderStructure.Fingerprint"/>), which ignores the vendor's
/// element ids. Those are local to one file, so two exports of the same rung number them
/// differently.
/// </para>
/// <para>
/// <b>The passes.</b> First, the longest run of identical networks still in order: these are
/// unchanged. The rest are paired by content. An identical network found elsewhere is moved. A
/// pair that is at least <see cref="SimilarityThreshold"/> alike is an edit of the same network.
/// Whatever is left was added or removed. A pair that stays in order relative to the unchanged
/// networks keeps its place; one that crossed them is drawn where it is now, with a thin row left
/// where it used to be.
/// </para>
/// <para>
/// <b>The case the threshold exists for</b> (the owner's question, 2026-10-05): a rung
/// deleted, and an unrelated one written in its place. Same position, little in common. Paired
/// as "changed", every element would light up as modified and the row would read as an edit of
/// the old rung, which it is not. Below the threshold the two are a removal and an addition. They
/// still share a row, because they share a place, and the row says exactly that.
/// </para>
/// </remarks>
public static class LadderDiff
{
    /// <summary>
    /// How alike two networks must be to count as one network edited, from 0 to 1.
    /// </summary>
    /// <remarks>
    /// Similarity is the mean of three Jaccard indices over the networks' elements: exact
    /// (kind, variant and operands), shape (kind and variant only) and operands only, plus
    /// <see cref="TitleBonus"/> for an equal non-empty title. One operand renamed in a
    /// three-element rung scores (0.5 + 1 + 0.5) / 3 ≈ 0.67, and so does one contact turned from NO
    /// to NC. An unrelated rung of the same shape scores (0 + 1 + 0) / 3 ≈ 0.33. Both sides of 0.6
    /// are pinned by tests.
    /// </remarks>
    public const double SimilarityThreshold = 0.6;

    /// <summary>What an equal, non-empty network title adds to <see cref="Similarity"/>.</summary>
    /// <remarks>
    /// A title is the one identity a person gave the network, so it counts for a lot. It was 0.1
    /// until the owner's scenarios (2026-10-05): "Motor run" kept its title, its first contact and
    /// its coil, swapped a permit contact for an edge contact and gained a set coil. That scored
    /// 0.38 + 0.1 and was drawn as a whole rung replaced, where anyone reading it sees one rung
    /// edited. At 0.25 it is 0.63. An unrelated rung of the same shape under the same title
    /// (0.33 + 0.25 = 0.58) still reads as replaced, and the owner's deleted-and-rewritten case
    /// changes its title, so it gets no bonus at all.
    /// </remarks>
    public const double TitleBonus = 0.25;

    public static LadderComparison Compare(LadderBody? old, LadderBody @new)
    {
        ArgumentNullException.ThrowIfNull(@new);

        if (old is null)
        {
            return new LadderComparison(
                @new.BlockName,
                @new.Networks.Select(n => new LadderRow(LadderChange.Added, null, Side(n, LdMark.Added))).ToList(),
                HasOldSide: false);
        }

        var o = old.Networks;
        var n = @new.Networks;
        var oldPrints = o.Select(LadderStructure.Fingerprint).ToList();
        var newPrints = n.Select(LadderStructure.Fingerprint).ToList();

        // Pass 1: unchanged and in order.
        var anchors = Lcs(oldPrints, newPrints, string.Equals);
        var usedOld = anchors.Select(a => a.Old).ToHashSet();
        var usedNew = anchors.Select(a => a.New).ToHashSet();

        // Pass 2: identical, elsewhere. Pass 3: alike enough to be the same network edited.
        var pairs = new List<(int Old, int New, bool Exact)>();
        for (var j = 0; j < n.Count; j++)
        {
            if (usedNew.Contains(j))
            {
                continue;
            }

            var i = Enumerable.Range(0, o.Count).FirstOrDefault(i => !usedOld.Contains(i) && oldPrints[i] == newPrints[j], -1);
            if (i >= 0)
            {
                pairs.Add((i, j, true));
                usedOld.Add(i);
                usedNew.Add(j);
            }
        }

        var candidates = (
            from i in Enumerable.Range(0, o.Count).Where(i => !usedOld.Contains(i))
            from j in Enumerable.Range(0, n.Count).Where(j => !usedNew.Contains(j))
            let score = Similarity(o[i], n[j])
            where score >= SimilarityThreshold
            orderby score descending, Math.Abs(i - j), i, j
            select (i, j)).ToList();
        foreach (var (i, j) in candidates)
        {
            if (!usedOld.Contains(i) && !usedNew.Contains(j))
            {
                pairs.Add((i, j, false));
                usedOld.Add(i);
                usedNew.Add(j);
            }
        }

        // A pair keeps its place when it sits in the same gap between unchanged networks on
        // both sides, and in order with the other pairs of that gap.
        int Gap(int index, Func<(int Old, int New), int> side) => anchors.Count(a => side(a) < index);

        var inPlace = new HashSet<(int Old, int New, bool Exact)>();
        foreach (var gap in pairs.Where(p => Gap(p.Old, a => a.Old) == Gap(p.New, a => a.New))
                     .GroupBy(p => Gap(p.New, a => a.New)))
        {
            foreach (var p in IncreasingRun(gap.OrderBy(p => p.New).ToList()))
            {
                inPlace.Add(p);
            }
        }

        var moved = pairs.Where(p => !inPlace.Contains(p)).ToList();
        var movedFrom = moved.ToDictionary(p => p.Old);
        var movedTo = moved.ToDictionary(p => p.New);
        var steps = anchors.Select(a => (a.Old, a.New, Exact: true))
            .Concat(inPlace)
            .OrderBy(p => p.New)
            .ToList();

        var rows = new List<LadderRow>();
        var previousOld = -1;
        var previousNew = -1;
        foreach (var step in steps.Append((Old: o.Count, New: n.Count, Exact: true)))
        {
            EmitGap(previousOld, step.Old, previousNew, step.New);
            if (step.Old < o.Count)
            {
                rows.Add(step.Exact
                    ? new LadderRow(LadderChange.Same, Side(o[step.Old], LdMark.Same), Side(n[step.New], LdMark.Same))
                    : Edited(LadderChange.Changed, o[step.Old], n[step.New]));
            }

            previousOld = step.Old;
            previousNew = step.New;
        }

        return new LadderComparison(@new.BlockName, rows, HasOldSide: true);

        void EmitGap(int oldFrom, int oldTo, int newFrom, int newTo)
        {
            var removed = new Queue<int>();
            for (var i = oldFrom + 1; i < oldTo; i++)
            {
                if (!usedOld.Contains(i))
                {
                    removed.Enqueue(i);
                }
            }

            var added = Enumerable.Range(newFrom + 1, Math.Max(0, newTo - newFrom - 1))
                .Where(j => !usedNew.Contains(j))
                .ToList();

            // Paired first, so that a rung deleted and another written in its place share a
            // row; what is left over keeps its own.
            var replacedBy = new Dictionary<int, int>();
            foreach (var j in added)
            {
                if (removed.Count == 0)
                {
                    break;
                }

                replacedBy[j] = removed.Dequeue();
            }

            for (var i = oldFrom + 1; i < oldTo; i++)
            {
                if (removed.Contains(i))
                {
                    rows.Add(new LadderRow(LadderChange.Removed, Side(o[i], LdMark.Removed), null));
                }
                else if (movedFrom.TryGetValue(i, out var away))
                {
                    rows.Add(new LadderRow(LadderChange.MovedAway, Side(o[i], LdMark.Same), null) { MovedTo = n[away.New].Number });
                }
            }

            for (var j = newFrom + 1; j < newTo; j++)
            {
                if (replacedBy.TryGetValue(j, out var i))
                {
                    rows.Add(new LadderRow(LadderChange.Replaced, Side(o[i], LdMark.Removed), Side(n[j], LdMark.Added)));
                }
                else if (!usedNew.Contains(j))
                {
                    rows.Add(new LadderRow(LadderChange.Added, null, Side(n[j], LdMark.Added)));
                }
                else if (movedTo.TryGetValue(j, out var m))
                {
                    rows.Add(m.Exact
                        ? new LadderRow(LadderChange.Moved, Side(o[m.Old], LdMark.Same), Side(n[j], LdMark.Same))
                        : Edited(LadderChange.MovedAndChanged, o[m.Old], n[j]));
                }
            }
        }
    }

    private static LadderSide Side(LadderNetwork network, LdMark mark) =>
        new(network, LadderStructure.Of(network).Elements.ToDictionary(e => e.Id, _ => mark, StringComparer.Ordinal));

    /// <summary>One network on both sides, its elements marked one by one.</summary>
    /// <remarks>
    /// Elements are matched as the longest run of identical ones in reading order, not by
    /// position in the tree: one contact inserted at the start of a rung would otherwise shift
    /// every position after it and mark the whole rung modified. Between matched elements, an
    /// old and a new element of the same kind are the same element modified (an operand
    /// renamed, a contact turned from NO to NC); the rest were added or removed.
    /// </remarks>
    private static LadderRow Edited(LadderChange change, LadderNetwork old, LadderNetwork @new)
    {
        var a = LadderStructure.Of(old).Elements;
        var b = LadderStructure.Of(@new).Elements;
        var oldMarks = a.ToDictionary(e => e.Id, _ => LdMark.Removed, StringComparer.Ordinal);
        var newMarks = b.ToDictionary(e => e.Id, _ => LdMark.Added, StringComparer.Ordinal);

        var (same, modified) = PairElements(old, @new);
        foreach (var (o, n) in same)
        {
            oldMarks[o.Id] = LdMark.Same;
            newMarks[n.Id] = LdMark.Same;
        }

        foreach (var (o, n) in modified)
        {
            oldMarks[o.Id] = LdMark.Modified;
            newMarks[n.Id] = LdMark.Modified;
        }

        return new LadderRow(change, new LadderSide(old, oldMarks), new LadderSide(@new, newMarks))
        {
            HeaderChanged = old.Title != @new.Title || old.Comment != @new.Comment,
        };
    }

    /// <summary>
    /// The elements of two versions of a network that are the same element: unchanged, or
    /// modified in place. Whatever is in neither list was removed (old) or added (new).
    /// </summary>
    /// <remarks>
    /// One answer for every caller: the marks on a row and the "was" lines of a merged drawing
    /// come from this, so the two can never disagree about which element became which.
    /// </remarks>
    internal static (List<(LdNode Old, LdNode New)> Same, List<(LdNode Old, LdNode New)> Modified) PairElements(
        LadderNetwork old, LadderNetwork @new)
    {
        var a = LadderStructure.Of(old).Elements;
        var b = LadderStructure.Of(@new).Elements;
        var same = Lcs(a.Select(LadderStructure.Describe).ToList(), b.Select(LadderStructure.Describe).ToList(), string.Equals);
        var modified = new List<(LdNode, LdNode)>();

        var previous = (Old: -1, New: -1);
        foreach (var anchor in same.Append((a.Count, b.Count)))
        {
            var olds = Enumerable.Range(previous.Old + 1, anchor.Item1 - previous.Old - 1).ToList();
            var news = Enumerable.Range(previous.New + 1, anchor.Item2 - previous.New - 1).ToList();
            foreach (var i in olds)
            {
                var kind = Kind(a[i]);
                var match = news.FirstOrDefault(j => Kind(b[j]) == kind, -1);
                if (match >= 0)
                {
                    modified.Add((a[i], b[match]));
                    news.Remove(match);
                }
            }

            previous = anchor;
        }

        return (same.Select(p => (a[p.Old], b[p.New])).ToList(), modified);

        static string Kind(LdNode node) => node switch
        {
            LdBox box => $"box:{box.TypeName}",
            _ => node.GetType().Name,
        };
    }

    /// <summary>How alike two networks are, from 0 to 1. See <see cref="SimilarityThreshold"/>.</summary>
    public static double Similarity(LadderNetwork a, LadderNetwork b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var x = LadderStructure.Of(a).Elements;
        var y = LadderStructure.Of(b).Elements;
        var score = (Jaccard(x.Select(LadderStructure.Describe), y.Select(LadderStructure.Describe))
                     + Jaccard(x.Select(LadderStructure.Shape), y.Select(LadderStructure.Shape))
                     + Jaccard(x.SelectMany(LadderStructure.Operands), y.SelectMany(LadderStructure.Operands))) / 3;
        if (a.Title.Length > 0 && a.Title == b.Title)
        {
            score += TitleBonus;
        }

        return Math.Min(1, score);
    }

    /// <summary>Jaccard index over multisets: shared items counted as often as both have them.</summary>
    private static double Jaccard(IEnumerable<string> a, IEnumerable<string> b)
    {
        var left = a.GroupBy(s => s, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var right = b.GroupBy(s => s, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var keys = left.Keys.Union(right.Keys, StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return 1;
        }

        var shared = keys.Sum(k => Math.Min(left.GetValueOrDefault(k), right.GetValueOrDefault(k)));
        var total = keys.Sum(k => Math.Max(left.GetValueOrDefault(k), right.GetValueOrDefault(k)));
        return (double)shared / total;
    }

    /// <summary>The longest common subsequence, as index pairs in order.</summary>
    private static List<(int Old, int New)> Lcs<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equal)
    {
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                table[i, j] = equal(a[i], b[j])
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (equal(a[x], b[y]))
            {
                pairs.Add((x++, y++));
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }

    /// <summary>
    /// The longest run of pairs whose old indices rise with their new ones: the ones that kept
    /// their order. Input is sorted by new index.
    /// </summary>
    private static List<(int Old, int New, bool Exact)> IncreasingRun(List<(int Old, int New, bool Exact)> pairs)
    {
        if (pairs.Count == 0)
        {
            return [];
        }

        var length = new int[pairs.Count];
        var back = new int[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            length[i] = 1;
            back[i] = -1;
            for (var k = 0; k < i; k++)
            {
                if (pairs[k].Old < pairs[i].Old && length[k] + 1 > length[i])
                {
                    length[i] = length[k] + 1;
                    back[i] = k;
                }
            }
        }

        var end = Array.IndexOf(length, length.Max());
        var run = new List<(int Old, int New, bool Exact)>();
        for (var i = end; i >= 0; i = back[i])
        {
            run.Add(pairs[i]);
        }

        run.Reverse();
        return run;
    }
}
