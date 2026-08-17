using System.Text;
using Versio.Core.Git;

namespace Versio.Core.Projects;

/// <summary>What a set of changed files did to an object.</summary>
/// <remarks>
/// Narrower than <see cref="ChangeKind"/> on purpose: git distinguishes copied from added and
/// conflicted from modified, and a commit subject does not. The word in front of a name has to
/// be one an engineer would have typed.
/// </remarks>
public enum ChangeVerb
{
    Modify,
    Add,
    Delete,
    Rename,
}

/// <summary>One object, and what happened to it across all the files it exports to.</summary>
public sealed record ObjectChange
{
    public required NamedObject Object { get; init; }

    public required ChangeVerb Verb { get; init; }
}

/// <summary>
/// The changed files, said the way an engineer would say them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller decides which changes to pass.</b> The summary line describes the working tree
/// and the drafted subject describes what is about to be committed, and those are different
/// lists once anything is staged. Choosing between them here would mean this type knowing about
/// the commit button's three behaviours; it takes a list and describes it.
/// </para>
/// <para>
/// <b>Counted by object, not by file.</b> A GVL exports to <c>.gvl.st</c> and <c>.gvl.xml</c>,
/// so editing one GVL produces two rows in git's output and would produce "2 GVLs" from any
/// count of paths. Distinct GUIDs, per <see cref="ProjectObject"/>'s own rule.
/// </para>
/// </remarks>
public static class ChangeSummary
{
    /// <summary>The longest subject that renders without a warning colour.</summary>
    /// <remarks>
    /// git's own convention, and the threshold the panel's subject counter already turns amber
    /// at. A drafted message that arrives over the limit would be advising against the rule the
    /// control beside it enforces.
    /// </remarks>
    public const int SubjectLimit = 50;

    /// <summary>Every object these changes touched, once each, with what happened to it.</summary>
    public static IReadOnlyList<ObjectChange> Objects(
        IReadOnlyList<FileChange> changes, ObjectIndex index)
    {
        var byGuid = new Dictionary<string, (NamedObject Object, List<ChangeKind> Kinds)>(
            StringComparer.OrdinalIgnoreCase);

        var order = new List<string>();

        foreach (var change in changes)
        {
            if (index.Lookup(change.Path) is not { } found)
            {
                continue;
            }

            // A manifest written before GUIDs were reliable, or an object the exporter wrote
            // without one, still deserves a row — keyed by name and type so two different
            // objects do not merge into one.
            var key = string.IsNullOrEmpty(found.Guid)
                ? $"{found.Project} {found.Type} {found.Name}"
                : found.Guid;

            if (!byGuid.TryGetValue(key, out var entry))
            {
                entry = (found, []);
                byGuid[key] = entry;
                order.Add(key);
            }

            entry.Kinds.Add(change.Effective);
        }

        return order
            .Select(k => byGuid[k])
            .Select(e => new ObjectChange { Object = e.Object, Verb = VerbFor(e.Kinds) })
            .ToList();
    }

    /// <summary>
    /// "3 POUs, 1 GVL and 1 DUT changed in RefrigeratorControl" — or empty when no changed
    /// file belongs to a project.
    /// </summary>
    /// <remarks>
    /// Empty rather than a sentence about files, because when nothing PLC changed the rows are
    /// already ordinary paths and a line restating their count would be a second way of saying
    /// what the list says. The caller hides the line on empty.
    /// </remarks>
    public static string Sentence(IReadOnlyList<FileChange> changes, ObjectIndex index)
    {
        var objects = Objects(changes, index);
        if (objects.Count == 0)
        {
            return string.Empty;
        }

        // Grouped by type alone, and the vendor comes from the group's first object. A
        // repository holding two toolchains that spell one type string the same way renders it
        // in the first group member's vocabulary - which is a rendering choice rather than a
        // correctness one, and the alternative is worse: grouping by (vendor, type) would split
        // "3 POUs" into "2 POUs" and "1 POU" for a reader who does not care whose POU it is.
        var byType = objects
            .GroupBy(o => o.Object.Type, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Type: g.Key, Count: g.Count(), g.First().Object.Vendor))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => ObjectTypeNames.Chip(g.Vendor, g.Type), StringComparer.OrdinalIgnoreCase)
            .Select(g => ObjectTypeNames.Counted(g.Vendor, g.Type, g.Count));

        var projects = objects
            .Select(o => o.Object.Project)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var where = projects.Count == 1
            ? projects[0]
            : $"{projects.Count} projects";

        var sentence = new StringBuilder()
            .Append(Join(byType))
            .Append(" changed in ")
            .Append(where);

        // Said rather than left out. A summary that counted only what it understood would read
        // as a complete account of the commit, and the file somebody forgot is exactly the one
        // that is not a PLC object.
        var others = changes.Count(c => index.Lookup(c.Path) is null);
        if (others > 0)
        {
            sentence.Append(", plus ").Append(others).Append(others == 1 ? " other file" : " other files");
        }

        return sentence.ToString();
    }

    /// <summary>
    /// A commit subject built from the objects: "Modify FB_Motor, GVL_Alarms; add DUT_Recipe".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Offered, never applied.</b> Generated export trees are where commit messages go to
    /// die — "update", "changes", "wip" — because writing a good one means reading a diff of
    /// machine-written files. This is the one place the tool knows more than a person in a
    /// hurry. It is still a draft: it fills a box somebody can edit, and nothing overwrites what
    /// they typed.
    /// </para>
    /// <para>
    /// <b>Truncation drops whole names.</b> Over <see cref="SubjectLimit"/> the tail becomes
    /// "and 4 more" rather than an ellipsis through the middle of an identifier — half a
    /// function block's name is not a shorter version of it, it is a different name that may
    /// well exist in the project.
    /// </para>
    /// </remarks>
    public static string DraftSubject(IReadOnlyList<FileChange> changes, ObjectIndex index)
    {
        var objects = Objects(changes, index);
        if (objects.Count == 0)
        {
            return string.Empty;
        }

        // Modify first because it is the common case, and a subject that opens with the rare
        // one reads as though the rare one is the point of the commit.
        var clauses = new[] { ChangeVerb.Modify, ChangeVerb.Add, ChangeVerb.Delete, ChangeVerb.Rename }
            .Select(verb => (Verb: verb, Names: objects
                .Where(o => o.Verb == verb)
                .Select(o => o.Object.Name)
                .ToList()))
            .Where(c => c.Names.Count > 0)
            .ToList();

        var total = clauses.Sum(c => c.Names.Count);

        for (var shown = total; shown >= 1; shown--)
        {
            var subject = Render(clauses, shown, total - shown);

            // The single-name case is rendered whatever its length: an object whose name alone
            // exceeds the limit has no shorter honest form, and "Modify 1 object" tells the
            // user less than the name they are about to commit.
            if (subject.Length <= SubjectLimit || shown == 1)
            {
                return subject;
            }
        }

        return string.Empty;
    }

    /// <summary>Render the first <paramref name="shown"/> names, with the rest as a count.</summary>
    private static string Render(
        IReadOnlyList<(ChangeVerb Verb, List<string> Names)> clauses, int shown, int hidden)
    {
        var parts = new List<string>();
        var left = shown;

        foreach (var clause in clauses)
        {
            if (left == 0)
            {
                break;
            }

            var take = Math.Min(left, clause.Names.Count);
            left -= take;
            parts.Add($"{Word(clause.Verb)} {string.Join(", ", clause.Names.Take(take))}");
        }

        var subject = string.Join("; ", parts);

        if (hidden > 0)
        {
            subject += $" and {hidden} more";
        }

        return char.ToUpperInvariant(subject[0]) + subject[1..];
    }

    private static string Word(ChangeVerb verb) => verb switch
    {
        ChangeVerb.Add => "add",
        ChangeVerb.Delete => "delete",
        ChangeVerb.Rename => "rename",
        _ => "modify",
    };

    /// <summary>
    /// What happened to an object, from what happened to each of its files.
    /// </summary>
    /// <remarks>
    /// Unanimity or nothing. An object whose <c>.st</c> was deleted and whose <c>.xml</c> was
    /// modified has not been deleted, and calling it deleted in a commit subject would describe
    /// a commit that did not happen. "Modify" is the one verb that is true of any mixture.
    /// </remarks>
    private static ChangeVerb VerbFor(IReadOnlyList<ChangeKind> kinds)
    {
        if (kinds.All(k => k is ChangeKind.Added or ChangeKind.Untracked))
        {
            return ChangeVerb.Add;
        }

        if (kinds.All(k => k == ChangeKind.Deleted))
        {
            return ChangeVerb.Delete;
        }

        if (kinds.All(k => k == ChangeKind.Renamed))
        {
            return ChangeVerb.Rename;
        }

        return ChangeVerb.Modify;
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();

        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            _ => $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}",
        };
    }
}
