namespace Versio.Core.Iec.Ladder;

/// <summary>
/// One network drawn once with both versions in it: kept elements as they are, added ones
/// marked added, removed ones still standing where they stood, marked removed.
/// </summary>
/// <param name="Root">The merged rung. Every node id is prefixed <c>o:</c> (from the old network) or <c>n:</c> (from the new).</param>
/// <param name="Wired">Both networks' wired pins under the prefixed ids, so a box still knows its power pins.</param>
/// <param name="Marks">What happened to each element, by prefixed id.</param>
/// <param name="Was">For a modified element, what it was, in a few words: <c>was "Pump_Ok"</c>, <c>was NO</c>, <c>was PT=T#5s</c>.</param>
public sealed record LadderMergedNetwork(
    LdBlock Root,
    ISet<LdPinRef> Wired,
    IReadOnlyDictionary<string, LdMark> Marks,
    IReadOnlyDictionary<string, string> Was);

/// <summary>
/// Merges two versions of one network into a single rung, the default way a changed network is
/// drawn (the owner, 2026-10-05: "if something was deleted it shows on the same rung as deleted,
/// so as a single view instead of side by side").
/// </summary>
/// <remarks>
/// <para>
/// <b>Merged as trees, not as graphs.</b> Both versions are first reduced to their series and
/// parallel shape (<see cref="LadderStructure"/>), and the shapes are aligned level by level.
/// Along a series, the longest run of identical items is kept. Between those, an old and a new
/// item of the same kind are one item modified, and a parallel block meets a parallel block
/// branch by branch. Whatever is left was removed (it stays in the rung, in place) or added.
/// Merging the graphs instead would need a correspondence between the two files' wires, which
/// do not share ids.
/// </para>
/// <para>
/// <b>A removed element stays on the rung.</b> Power is drawn through it, which is not how the
/// PLC runs the new version, and that is the point of the view: the reader sees where it stood.
/// The side-by-side view stays one tick away for anyone who wants each version as it runs.
/// </para>
/// <para>
/// <b>Null when either side has no series/parallel shape.</b> There is then no tree to align,
/// and the caller draws the two versions one under the other rather than a merge that guesses.
/// </para>
/// </remarks>
public static class LadderMerge
{
    public const string OldPrefix = "o:";
    public const string NewPrefix = "n:";

    public static LadderMergedNetwork? Merge(LadderNetwork old, LadderNetwork @new)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(@new);

        var a = LadderStructure.Of(old);
        var b = LadderStructure.Of(@new);
        // A function block diagram is not merged (yet): it has one wire per input pin, so a
        // removed subtree has no wire to hang on beside the one that replaced it. Its changed
        // networks are drawn as two stacked diagrams, each marked, which is honest if longer.
        if (old.Language != LdLanguage.Ladder || @new.Language != LdLanguage.Ladder
            || !a.IsSeriesParallel || !b.IsSeriesParallel)
        {
            return null;
        }

        var state = new State();
        LdBlock root = (a.Root, b.Root) switch
        {
            (null, null) => new LdSeries([]),
            (null, { } n) => state.Added(n),
            ({ } o, null) => state.Removed(o),
            ({ } o, { } n) => state.Series(o, n),
        };

        // Refused when it would repeat itself: an element marked removed while an identical one
        // is still on the rung, more often than the old version had it to spare. That happens
        // when the rung's shape changed under the edit (delete the contact in front of a nested
        // branch and that branch folds into its parent, so no branch pairs with it), and two
        // stacked rungs say that more truthfully than one rung showing a contact twice. Found by
        // the merge property test, seed 48.
        if (RepeatsItself(old, @new, root, state.Marks))
        {
            return null;
        }

        var wired = new HashSet<LdPinRef>(
            old.Nets.SelectMany(n => n.Ends).Select(e => e with { NodeId = OldPrefix + e.NodeId })
                .Concat(@new.Nets.SelectMany(n => n.Ends).Select(e => e with { NodeId = NewPrefix + e.NodeId })));
        return new LadderMergedNetwork(root, wired, state.Marks, state.Was);
    }

    private static bool RepeatsItself(
        LadderNetwork old, LadderNetwork @new, LdBlock root, IReadOnlyDictionary<string, LdMark> marks)
    {
        static Dictionary<string, int> Count(IEnumerable<LdNode> nodes) =>
            nodes.GroupBy(LadderStructure.Describe).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var before = Count(LadderStructure.Of(old).Elements);
        var after = Count(LadderStructure.Of(@new).Elements);
        var removed = Count(LadderStructure.Walk(root).Where(n => marks[n.Id] == LdMark.Removed));
        return removed.Any(r =>
            r.Value > Math.Max(0, before.GetValueOrDefault(r.Key) - after.GetValueOrDefault(r.Key)));
    }

    private sealed class State
    {
        public Dictionary<string, LdMark> Marks { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Was { get; } = new(StringComparer.Ordinal);

        public LdBlock Added(LdBlock block) => Mark(block, NewPrefix, LdMark.Added);

        public LdBlock Removed(LdBlock block) => Mark(block, OldPrefix, LdMark.Removed);

        private LdBlock Same(LdBlock block) => Mark(block, NewPrefix, LdMark.Same);

        private LdBlock Mark(LdBlock block, string prefix, LdMark mark) => block switch
        {
            LdLeaf leaf => Leaf(leaf.Node, prefix, mark),
            LdSeries s => new LdSeries(s.Items.Select(i => Mark(i, prefix, mark)).ToList()),
            LdParallel p => new LdParallel(p.Branches.Select(br => Mark(br, prefix, mark)).ToList()),
            _ => block,
        };

        private LdLeaf Leaf(LdNode node, string prefix, LdMark mark)
        {
            var renamed = node with { Id = prefix + node.Id };
            Marks[renamed.Id] = mark;
            return new LdLeaf(renamed);
        }

        /// <summary>Two blocks merged as series: a block that is not a series is a series of one.</summary>
        public LdBlock Series(LdBlock old, LdBlock @new)
        {
            var o = old is LdSeries so ? so.Items : [old];
            var n = @new is LdSeries sn ? sn.Items : [@new];
            var merged = Align(o, n);
            return merged.Count == 1 ? merged[0] : new LdSeries(merged);
        }

        private LdBlock Parallel(LdParallel old, LdParallel @new)
        {
            // A branch is a series of its own, so a matched pair merges as one; a branch only one
            // side has is a whole branch added or removed, still drawn as a branch.
            var branches = Align(old.Branches, @new.Branches, asBranches: true);
            return new LdParallel(branches);
        }

        /// <summary>
        /// Aligns two lists of items: identical runs kept, compatible items merged in place, the
        /// rest added or removed where they stand.
        /// </summary>
        private List<LdBlock> Align(IReadOnlyList<LdBlock> old, IReadOnlyList<LdBlock> @new, bool asBranches = false)
        {
            var keys = (Old: old.Select(Key).ToList(), New: @new.Select(Key).ToList());
            var anchors = Lcs(keys.Old, keys.New);
            var result = new List<LdBlock>();
            int i = 0, j = 0;
            foreach (var (ai, aj) in anchors.Append((old.Count, @new.Count)))
            {
                Gap(old.Skip(i).Take(ai - i).ToList(), @new.Skip(j).Take(aj - j).ToList(), result, asBranches);
                if (ai < old.Count)
                {
                    result.Add(Same(@new[aj]));
                }

                i = ai + 1;
                j = aj + 1;
            }

            return result;
        }

        /// <summary>
        /// The stretch between two identical items: pairs chosen by the best total score, the
        /// rest removed or added in reading order.
        /// </summary>
        /// <remarks>
        /// Scored, not first-fit. First-fit paired an inserted contact with the old parallel block
        /// that happened to come first (one of its branches was also a contact), and every pairing
        /// after it in that stretch shifted by one: half a rung drawn as removed beside its own
        /// copy added (seed 7 of the merge property test, 2026-10-05).
        /// </remarks>
        private void Gap(List<LdBlock> old, List<LdBlock> @new, List<LdBlock> result, bool asBranches)
        {
            var best = new double[old.Count + 1, @new.Count + 1];
            for (var i = old.Count - 1; i >= 0; i--)
            {
                for (var j = @new.Count - 1; j >= 0; j--)
                {
                    var score = Score(old[i], @new[j], asBranches);
                    best[i, j] = Math.Max(
                        Math.Max(best[i + 1, j], best[i, j + 1]),
                        score > 0 ? score + best[i + 1, j + 1] : double.MinValue);
                }
            }

            int x = 0, y = 0;
            while (x < old.Count || y < @new.Count)
            {
                if (x < old.Count && y < @new.Count
                    && Score(old[x], @new[y], asBranches) is var s && s > 0
                    && Math.Abs(best[x, y] - (s + best[x + 1, y + 1])) < 1e-9)
                {
                    result.Add(Pair(old[x++], @new[y++]));
                }
                else if (x < old.Count && (y == @new.Count || Math.Abs(best[x, y] - best[x + 1, y]) < 1e-9))
                {
                    result.Add(Removed(old[x++]));
                }
                else
                {
                    result.Add(Added(@new[y++]));
                }
            }
        }

        /// <summary>How well an old and a new item stand for one edited item; zero when they do not.</summary>
        private static double Score(LdBlock old, LdBlock @new, bool asBranches) => (old, @new) switch
        {
            (LdLeaf a, LdLeaf b) => Kind(a.Node) == Kind(b.Node) ? 0.5 + (0.4 * Overlap(old, @new)) : 0,

            // A branch opened around a contact (a seal-in added) or closed again: the contact is
            // the one branch the two sides share, not a removal beside a new block. Only when the
            // branch IS that contact: the same kind with another operand is another contact.
            (LdLeaf a, LdParallel b) => b.Branches.Any(br => Key(br) == Key(a)) ? 0.9 : 0,
            (LdParallel a, LdLeaf b) => a.Branches.Any(br => Key(br) == Key(b)) ? 0.9 : 0,

            // Two parallel blocks, or two branches, are one edited block when they hold at least one
            // identical element: a branch that gained three contacts is still that branch. Any
            // lower bar than "one shared" draws that shared element twice, removed beside added,
            // which is worse than pairing two blocks loosely; the best total score still decides
            // between candidates.
            (LdParallel, LdParallel) => Overlap(old, @new) is var p && p > 0 ? 0.25 + p : 0,
            _ when asBranches => Overlap(old, @new) is var q && q > 0 ? 0.25 + q : 0,
            _ => 0,
        };

        /// <summary>Jaccard over the elements two blocks hold, by what they draw.</summary>
        private static double Overlap(LdBlock a, LdBlock b)
        {
            var left = LadderStructure.Walk(a).Select(LadderStructure.Describe).ToList();
            var right = LadderStructure.Walk(b).Select(LadderStructure.Describe).ToList();
            var shared = left.GroupBy(k => k).Sum(g => Math.Min(g.Count(), right.Count(r => r == g.Key)));
            var total = left.Count + right.Count - shared;
            return total == 0 ? 1 : (double)shared / total;
        }

        private LdBlock Pair(LdBlock old, LdBlock @new) => (old, @new) switch
        {
            (LdLeaf a, LdLeaf b) => Modified(a.Node, b.Node),
            (LdParallel a, LdParallel b) => Parallel(a, b),
            (LdLeaf a, LdParallel b) => Parallel(new LdParallel([a]), b),
            (LdParallel a, LdLeaf b) => Parallel(a, new LdParallel([b])),
            _ => Series(old, @new),
        };

        private LdLeaf Modified(LdNode old, LdNode @new)
        {
            if (LadderStructure.Describe(old) == LadderStructure.Describe(@new))
            {
                return Leaf(@new, NewPrefix, LdMark.Same);
            }

            var leaf = Leaf(@new, NewPrefix, LdMark.Modified);
            Was[leaf.Node.Id] = Describe(old, @new);
            return leaf;
        }

        /// <summary>What a modified element was, in the fewest words that say what changed.</summary>
        private string Describe(LdNode old, LdNode @new)
        {
            switch (old, @new)
            {
                case (LdContact a, LdContact b) when a.Operand != b.Operand:
                    return "was " + a.Operand;
                case (LdContact a, LdContact b) when a.Kind != b.Kind:
                    return "was " + Short(a.Kind);
                case (LdCoil a, LdCoil b) when a.Operand != b.Operand:
                    return "was " + a.Operand;
                case (LdCoil a, LdCoil b) when a.Kind != b.Kind:
                    return "was " + Short(a.Kind);
                case (LdJump a, LdJump b) when a.Target != b.Target:
                    return "was " + a.Target;
                case (LdBox a, LdBox b):
                    var changed = a.Pins
                        .Where(p => b.Pins.FirstOrDefault(q => q.Name == p.Name) is { } q
                                    && (q.Operand != p.Operand || q.Negated != p.Negated))
                        .Select(p => $"{p.Name}={p.Operand}")
                        .ToList();
                    if (a.Instance != b.Instance)
                    {
                        changed.Insert(0, "instance " + a.Instance);
                    }

                    if (a.Detail != b.Detail)
                    {
                        changed.Insert(0, a.Detail ?? "");
                    }

                    return changed.Count > 0 ? "was " + string.Join(", ", changed) : "pins changed";
                default:
                    return "changed";
            }
        }

        private static string Short(LdContactKind kind) => kind switch
        {
            LdContactKind.NormallyOpen => "NO",
            LdContactKind.NormallyClosed => "NC",
            LdContactKind.PositiveEdge => "P",
            _ => "N",
        };

        private static string Short(LdCoilKind kind) => kind switch
        {
            LdCoilKind.Normal => "( )",
            LdCoilKind.Negated => "(/)",
            LdCoilKind.Set => "(S)",
            LdCoilKind.Reset => "(R)",
            LdCoilKind.PositiveEdge => "(P)",
            _ => "(N)",
        };

        private static string Kind(LdNode node) => node switch
        {
            LdBox box => $"box:{box.TypeName}:{box.InlineCompare}",
            _ => node.GetType().Name,
        };

        /// <summary>A block as comparable text: identical exactly when it draws the same.</summary>
        private static string Key(LdBlock block) => block switch
        {
            LdLeaf leaf => LadderStructure.Describe(leaf.Node),
            LdSeries s => "S(" + string.Join(",", s.Items.Select(Key)) + ")",
            LdParallel p => "P(" + string.Join("|", p.Branches.Select(Key)) + ")",
            _ => "?",
        };

        private static List<(int, int)> Lcs(List<string> a, List<string> b)
        {
            var table = new int[a.Count + 1, b.Count + 1];
            for (var x = a.Count - 1; x >= 0; x--)
            {
                for (var y = b.Count - 1; y >= 0; y--)
                {
                    table[x, y] = a[x] == b[y] ? table[x + 1, y + 1] + 1 : Math.Max(table[x + 1, y], table[x, y + 1]);
                }
            }

            var pairs = new List<(int, int)>();
            int i = 0, j = 0;
            while (i < a.Count && j < b.Count)
            {
                if (a[i] == b[j])
                {
                    pairs.Add((i++, j++));
                }
                else if (table[i + 1, j] >= table[i, j + 1])
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            return pairs;
        }
    }
}
