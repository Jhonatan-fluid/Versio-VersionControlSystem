using System.Text.Json;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// <c>versio_tree</c>'s name walk, and the one thing it is opinionated about: a folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half of the foldered-member fix that does not need CODESYS.</b> The
/// exporter's side — a method inside a folder inside a POU is written beside its owner — can
/// only be proven by exporting a real project, which costs half an hour. The importer's side
/// is a tree walk over objects that answer three questions, so a stand-in object answers them
/// here and the rule is exercised in under a second.
/// </para>
/// <para>
/// <b>What it is protecting.</b> A resolve chain from the manifest names objects, and a
/// folder is not one of the things it names. A walk that insisted on the folder would answer
/// "not found" for <c>FB_Alarm/_M_Reset</c>, and an import that cannot find the object it was
/// told to update <i>creates</i> it — a second <c>_M_Reset</c> beside the first, which is
/// worse than the failure it came from.
/// </para>
/// <para>
/// Runs under CPython, which is not the IronPython 2.7 that runs inside CODESYS. Everything
/// here is attribute access and list building, where the two agree; the round trip through a
/// real toolchain is <c>ProjectTreeFromRealExportTests</c>'s job.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PythonTreeWalkTests(ITestOutputHelper output)
{
    /// <summary>
    /// Walk <paramref name="path"/> over a tree built from <paramref name="treeJson"/>.
    /// </summary>
    /// <remarks>
    /// The stand-in answers <c>get_children()</c>, <c>get_name()</c> and <c>is_folder</c> and
    /// nothing else, which is exactly the surface <c>versio_tree</c> is allowed to touch — so
    /// a lookup that reached for anything richer would fail here rather than inside CODESYS.
    /// The <c>safe</c> passed in is the importer's own: swallow and fall back.
    /// </remarks>
    private string Walk(object tree, IReadOnlyList<string> path)
    {
        // MaxDepth, because the depth-cap case builds a folder chain deeper than
        // System.Text.Json's default of 64 — an object and an array per level, so the
        // default runs out at 32 folders, which is precisely the region being tested.
        var produced = CPythonHarness.RunJson(
            JsonSerializer.Serialize(new { tree, path }, new JsonSerializerOptions { MaxDepth = 256 }),
            """
            import versio_tree


            class Node(object):
                def __init__(self, spec):
                    self.name = spec["name"]
                    self.folder = spec.get("folder", False)
                    self.raises = spec.get("raises", False)
                    self.kids = [Node(k) for k in spec.get("children", [])]

                # A PROPERTY, not an attribute, and one that can throw — which is what a
                # live ScriptObject is. An attribute would make `safe` decorative here and
                # the raising case untestable.
                @property
                def is_folder(self):
                    if self.raises:
                        raise RuntimeError("this object does not answer that")
                    return self.folder

                def get_name(self):
                    return self.name

                def get_children(self):
                    return self.kids


            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default


            found = versio_tree.find_by_path(
                Node(payload["tree"]), payload["path"], safe)
            out = "<none>" if found is None else found.get_name()
            """,
            jsonOut: false);

        output.WriteLine($"{string.Join("/", path)} -> {produced}");
        return produced;
    }

    /// <summary>FB_Alarm with one method beside a folder holding another.</summary>
    /// <remarks>
    /// The shape the owner reported, spelled out: <c>M_Reset</c> is a direct member and
    /// <c>_M_Reset</c> was put in a <c>Private</c> folder, and CODESYS treats the two as
    /// equally FB_Alarm's.
    /// </remarks>
    private static object Application() => new
    {
        name = "Application",
        children = new object[]
        {
            new
            {
                name = "FB_Alarm",
                children = new object[]
                {
                    new { name = "M_Reset" },
                    new
                    {
                        name = "Private",
                        folder = true,
                        children = new object[]
                        {
                            new { name = "_M_Reset" },
                            new
                            {
                                name = "Deeper",
                                folder = true,
                                children = new object[] { new { name = "_M_Buried" } },
                            },
                        },
                    },
                },
            },
            new { name = "PLC_PRG" },
        },
    };

    [SkippableFact]
    public void A_direct_child_is_found_the_way_it_always_was()
    {
        // The non-vacuous floor. Everything below relaxes the walk, and a relaxation that
        // also changed the ordinary answer would be a much bigger change than the one being
        // made.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("PLC_PRG", Walk(Application(), ["PLC_PRG"]));
        Assert.Equal("M_Reset", Walk(Application(), ["FB_Alarm", "M_Reset"]));
    }

    [SkippableFact]
    public void A_member_inside_a_folder_answers_to_its_owner()
    {
        // THE CASE. The chain the planner builds from the manifest is FB_Alarm/_M_Reset,
        // because the manifest records an owner GUID and knows nothing about the folder the
        // IDE shows it in.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("_M_Reset", Walk(Application(), ["FB_Alarm", "_M_Reset"]));

        // Nested folders too — a folder inside a folder is the same non-place twice.
        Assert.Equal("_M_Buried", Walk(Application(), ["FB_Alarm", "_M_Buried"]));
    }

    [SkippableFact]
    public void The_folder_itself_is_still_findable_by_name()
    {
        // Project-level folders ARE directories in the mirror and DO appear in a resolve
        // chain, so making the walk see through folders must not make it unable to see them.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("Private", Walk(Application(), ["FB_Alarm", "Private"]));
        Assert.Equal("_M_Reset", Walk(Application(), ["FB_Alarm", "Private", "_M_Reset"]));
    }

    [SkippableFact]
    public void Only_folders_are_looked_through()
    {
        // The limit that keeps the relaxation honest. If a POU were walked past, a chain
        // naming a program would be answered by somebody's method — and for an import that
        // means applying a POU's text to a method.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("<none>", Walk(Application(), ["_M_Reset"]));
        Assert.Equal("<none>", Walk(Application(), ["PLC_PRG", "M_Reset"]));
    }

    [SkippableFact]
    public void An_ambiguous_name_is_refused_rather_than_guessed()
    {
        // Two objects of one name in two folders is not something CODESYS will compile, so
        // this is a shape the walk should never meet. It is pinned anyway, because "cannot
        // happen" is what every silent overwrite was called beforehand: the walk hands back
        // nothing, which is exactly what it did before it could see through folders at all.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        var ambiguous = new
        {
            name = "Application",
            children = new object[]
            {
                new { name = "A", folder = true, children = new object[] { new { name = "Dup" } } },
                new { name = "B", folder = true, children = new object[] { new { name = "Dup" } } },
            },
        };

        Assert.Equal("<none>", Walk(ambiguous, ["Dup"]));

        // Named through its folder, it is not ambiguous at all and must still resolve.
        Assert.Equal("Dup", Walk(ambiguous, ["A", "Dup"]));
    }

    [SkippableFact]
    public void A_direct_child_wins_over_one_inside_a_folder()
    {
        // Ordering, not preference. The direct child is what the chain literally says; the
        // folder search is a fallback for chains that omit a folder, and a fallback that
        // could outrank the literal reading is not a fallback.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        var both = new
        {
            name = "Application",
            children = new object[]
            {
                new
                {
                    name = "F",
                    folder = true,
                    children = new object[] { new { name = "Target", children = new object[] { new { name = "inner" } } } },
                },
                new
                {
                    name = "Target",
                    children = new object[] { new { name = "direct" } },
                },
            },
        };

        // The folder comes first in the child list, so a walk that searched folders first
        // would answer with the wrong one and this would fail.
        Assert.Equal("direct", Walk(both, ["Target", "direct"]));
    }

    [SkippableFact]
    public void A_name_that_is_nowhere_is_still_nowhere()
    {
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("<none>", Walk(Application(), ["FB_Alarm", "M_Nothing"]));
        Assert.Equal("<none>", Walk(Application(), ["Nothing", "M_Reset"]));
    }

    [SkippableFact]
    public void An_object_that_raises_when_asked_costs_that_object_and_nothing_else()
    {
        // `is_folder` is a property on a live ScriptObject and properties on this API raise —
        // that is the whole reason every access here goes through `safe`. A walk that let one
        // awkward sibling take the exception would fail a lookup that has nothing to do with
        // it, which is how one broken object becomes a failed import.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        var withAnAngryChild = new
        {
            name = "Application",
            children = new object[]
            {
                new { name = "Grumpy", raises = true },
                new { name = "F", folder = true, children = new object[] { new { name = "Target" } } },
            },
        };

        Assert.Equal("Target", Walk(withAnAngryChild, ["Target"]));
    }

    [SkippableFact]
    public void A_folder_chain_deeper_than_the_cap_stops_rather_than_running_forever()
    {
        // The cap the neighbouring walks all carry, for the reason they carry it: a cycle in
        // the object tree hangs the run with no output, and a hung import is worse than a
        // refused one. 32 is far past anything real, so this builds an unreal tree to reach
        // it — and the object one level inside the cap must still be found, or the guard
        // would be silently truncating ordinary work.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("Deep", Walk(FolderChain(30, "Deep"), ["Deep"]));
        Assert.Equal("<none>", Walk(FolderChain(40, "Deep"), ["Deep"]));
    }

    [SkippableFact]
    public void A_rename_retargets_the_tasks_of_its_own_application_only()
    {
        // Two Applications, each with a program called Simulation and a task that runs it.
        // The retargeting after a rename walked the whole project and repointed every task
        // entry carrying the name, so renaming ApplicationA's Simulation rewrote
        // ApplicationB's task to call a program it does not have (the 2026-10-24 review's
        // item 31). The walk starts at the renamed object's own Application now, found
        // through `parent`, and a nested Application's tasks are its own.
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        var produced = CPythonHarness.RunJson("{}", """
            import versio_tree


            class Node(object):
                def __init__(self, name, kind="", children=None, pous=None):
                    self.name = name
                    self.type = kind
                    self.kids = children or []
                    self.pous = pous
                    self.parent = None
                    for k in self.kids:
                        k.parent = self

                def get_name(self):
                    return self.name

                def get_children(self):
                    return self.kids


            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default


            sim_a = Node("Simulation", "POU")
            task_a = Node("MainTask", "TASK", pous=["Simulation", "PLC_PRG"])
            app_a = Node("ApplicationA", "APPLICATION", [Node("Folder", "FOLDER", [sim_a]), Node("Task Configuration", "TASKCONFIG", [task_a])])
            task_b = Node("MainTask", "TASK", pous=["Simulation"])
            nested_task = Node("ChildTask", "TASK", pous=["Simulation"])
            app_b = Node("ApplicationB", "APPLICATION", [Node("Simulation", "POU"), task_b, Node("Child", "APPLICATION", [nested_task])])
            project = Node("Project", "PROJECT", [Node("Device", "DEVICE", [Node("Plc Logic", "PLCLOGIC", [app_a, app_b])])])
            loose = Node("Loose", "POU")
            project.kids.append(loose); loose.parent = project

            is_app = lambda o: o.type == "APPLICATION"
            scope = versio_tree.application_of(sim_a, safe, is_app)
            tasks = [t.get_name() + "@" + t.parent.parent.get_name() for t in versio_tree.tasks_under(scope, safe)]
            none = versio_tree.application_of(loose, safe, is_app)
            b_tasks = [t.get_name() for t in versio_tree.tasks_under(app_b, safe)]
            out = "|".join([scope.get_name(), ",".join(tasks), "<none>" if none is None else none.get_name(), ",".join(b_tasks)])
            """,
            jsonOut: false);

        output.WriteLine(produced);
        // ApplicationA's task only; a loose object has no Application; ApplicationB's walk
        // includes the nested Application's task (it is under B) but A's walk never saw B.
        Assert.Equal("ApplicationA|MainTask@ApplicationA|<none>|MainTask,ChildTask", produced);
    }

    /// <summary>A folder nested <paramref name="depth"/> times with one object at the bottom.</summary>
    private static object FolderChain(int depth, string leaf)
    {
        object node = new { name = leaf };
        for (var i = 0; i < depth; i++)
        {
            node = new { name = $"f{i}", folder = true, children = new[] { node } };
        }

        return new { name = "Application", children = new[] { node } };
    }
}
