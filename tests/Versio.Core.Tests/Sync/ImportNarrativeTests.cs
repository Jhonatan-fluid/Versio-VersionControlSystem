using Versio.Core.Plc;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// The wording on the one dialog that precedes writing into a binary file.
/// </summary>
/// <remarks>
/// Asserted rather than eyeballed for the same reason <c>SyncNarrativeTests</c> exists: this
/// is the last thing somebody reads before a destructive, hard-to-inspect operation, so the
/// sentences are part of the feature.
/// </remarks>
public class ImportNarrativeTests
{
    private static PlannedFile Applied(string path) =>
        new() { Path = path, Action = PlannedAction.ApplyText };

    /// <summary>An object the import will actually remove.</summary>
    private static PlannedDeletion Gone(string name) => new()
    {
        Guid = name, Name = name,
        Files = [new DeletedFile($"App/{name}.st", "sha256:0", "ST_FULL")],
    };

    /// <summary>An object that has lost every file but that the script will refuse to remove.</summary>
    private static PlannedDeletion Kept(string name, string why = "Versio cannot check it") =>
        new()
        {
            Guid = name, Name = name,
            Files = [new DeletedFile($"App/{name}.gvl.xml", "sha256:0", "NATIVE")],
            CannotVerify = why,
        };

    [Fact]
    public void An_ordinary_import_says_how_much_it_will_apply()
    {
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files =
            [
                Applied("App/A.st"), Applied("App/B.st"),
                new PlannedFile
                {
                    Path = "_manifest.json",
                    Action = PlannedAction.Skip,
                    Skip = PlannedSkip.Bookkeeping,
                },
            ],
        });

        // Skips are not counted. Bookkeeping is in every mirror, and a number that always
        // includes two files nobody edited is a number nobody can check against what they did.
        Assert.Equal("2 files will be applied.", text);
    }

    [Fact]
    public void A_new_object_is_called_out_because_it_is_not_an_edit()
    {
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [Applied("App/A.st"), new PlannedFile
            {
                Path = "App/New.st", Action = PlannedAction.Create, Name = "New",
            }],
        });

        Assert.Equal("2 files will be applied, including 1 new object.", text);
    }

    /// <summary>An object whose file changed name, matched to a rename rather than a rebuild.</summary>
    private static PlannedRename Moved(
        string oldName, string newName, string[]? newParent = null) => new()
        {
            Guid = oldName,
            Resolve = ["App", oldName],
            OldName = oldName,
            NewName = newName,
            NewParent = newParent ?? ["App"],
            OldPath = $"App/{oldName}.st",
            NewPath = $"{string.Join("/", newParent ?? ["App"])}/{newName}.st",
        };

    [Fact]
    public void A_renamed_object_is_named_with_both_names_and_promises_only_what_holds()
    {
        // The reassuring half, and it comes FIRST on purpose: somebody who renamed a file is
        // scanning this dialog for their object's name, and finding it in a deletion sentence
        // is the moment they cancel an import that was going to do the right thing.
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [],
            Renames = [Moved("Old", "New")],
        });

        Assert.Contains("1 object will be renamed", text, StringComparison.Ordinal);
        Assert.Contains("Old → New", text, StringComparison.Ordinal);
        Assert.Contains("the same object is kept", text, StringComparison.Ordinal);

        // IT PROMISES THE TASK REPOINT, because that one is now made true rather than assumed.
        // The sentence said "references and task assignments survive" on the strength of the
        // GUID surviving, and a test went looking and found the opposite — a task's POU list
        // is stored by name and rename() does not rewrite it. The import repoints those lists
        // itself now, so the claim holds.
        Assert.Contains("any task that ran it is repointed", text, StringComparison.Ordinal);

        // AND IT STILL MUST NOT CLAIM "references" IN GENERAL. What else refers to an object
        // by name — a visualisation binding, say — has not been measured, and the vaguer word
        // is how the original false claim got in.
        Assert.DoesNotContain("references", text, StringComparison.Ordinal);

        // And it must NOT read as a deletion, which is what it would have been without the
        // pairing — the whole point of Phase 3.
        Assert.DoesNotContain("will be removed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_is_called_a_move_and_a_rename_a_rename()
    {
        // Being told "renamed" about a file that only changed folder reads as a bug in the
        // tool, and the reverse hides the fact that a name a colleague greps for has changed.
        var moved = ImportNarrative.Describe(new ImportPlan
        {
            Files = [], Renames = [Moved("Same", "Same", ["Lib"])],
        });
        Assert.Contains("will be moved", moved, StringComparison.Ordinal);

        var renamed = ImportNarrative.Describe(new ImportPlan
        {
            Files = [], Renames = [Moved("Old", "New")],
        });
        Assert.Contains("will be renamed", renamed, StringComparison.Ordinal);

        var both = ImportNarrative.Describe(new ImportPlan
        {
            Files = [], Renames = [Moved("Old", "New"), Moved("Same", "Same", ["Lib"])],
        });
        Assert.Contains("will be renamed or moved", both, StringComparison.Ordinal);
    }

    [Fact]
    public void Objects_the_import_will_remove_are_named_and_said_to_be_going()
    {
        // THIS TEST USED TO ASSERT THE OPPOSITE, and that is the whole point of it.
        //
        // It was written while deletions were planned and executed by nothing, so it pinned
        // "they will stay" and the ABSENCE of "will be removed" — under a comment saying that
        // when the script grew remove() this test was what would say the wording had to change
        // with it. The script grew remove(). This stayed green, because an assertion on the
        // old sentence cannot fail when the behaviour moves; it can only fail when somebody
        // fixes the sentence. For one commit the confirmation dialog told people their POU
        // would stay and then the import deleted it.
        //
        // So the pair below is deliberately two-sided: one assertion that the true sentence is
        // there, one that the false one is gone. A tripwire that only points one way is how
        // this got out.
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [Applied("App/A.st")],
            Deletions = [Gone("FB_Motor"), Gone("Signals")],
        });

        Assert.Contains("FB_Motor, Signals", text, StringComparison.Ordinal);
        Assert.Contains("will be removed from the project", text, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("will stay", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_the_script_will_refuse_to_remove_is_said_to_be_staying()
    {
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [Applied("App/A.st")],
            Deletions = [Kept("Glob_Var", "The export recorded no comparable hash")],
        });

        Assert.Contains("will stay in the project: Glob_Var", text, StringComparison.Ordinal);
        Assert.Contains("The export recorded no comparable hash", text, StringComparison.Ordinal);

        // The other half of the two-sided pair. A dialog naming an object as doomed and then
        // leaving it there is the same lie pointed the other way, and it is the one the
        // planner's own refusal makes reachable.
        Assert.DoesNotContain("will be removed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mixed_plan_says_both_things_and_does_not_merge_them()
    {
        // The case that makes the split load-bearing rather than cosmetic. A GVL and a POU can
        // lose their files in the same commit, and one of them will go while the other stays;
        // a single sentence covering both would be wrong about one of them whichever tense it
        // picked, and it would be wrong SILENTLY, because the dialog is the only place the
        // difference is ever visible before CODESYS starts.
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [Applied("App/A.st")],
            Deletions = [Gone("FB_Motor"), Kept("Glob_Var", "It is native")],
        });

        Assert.Contains("will be removed from the project: FB_Motor", text, StringComparison.Ordinal);
        Assert.Contains("will stay in the project: Glob_Var", text, StringComparison.Ordinal);

        // Neither name appears in the other's sentence.
        var removing = text[text.IndexOf("will be removed", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Glob_Var", removing[..removing.IndexOf("will stay", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void Two_different_reasons_for_keeping_get_two_different_sentences()
    {
        // Grouped by reason, because the reasons are not interchangeable: one is fixed by
        // exporting again and the other is not fixed by anything the reader can do. Telling
        // somebody to re-export when that will not help is its own small lie.
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [],
            Deletions = [Kept("Glob_Var", "No comparable hash"), Kept("Tasks", "Cannot rebuild it")],
        });

        Assert.Contains("No comparable hash", text, StringComparison.Ordinal);
        Assert.Contains("Cannot rebuild it", text, StringComparison.Ordinal);
        Assert.Contains("Glob_Var", text, StringComparison.Ordinal);
        Assert.Contains("Tasks", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_object_is_named_however_many_there_are()
    {
        // No cap, deliberately — the import plan's own decision. Empty the mirror and the
        // dialog lists everything, because a threshold makes the most destructive case the
        // one that says least.
        var many = Enumerable.Range(1, 40).Select(i => Gone($"Obj{i:D2}")).ToList();

        var text = ImportNarrative.Describe(new ImportPlan { Files = [], Deletions = many });

        Assert.Contains("Obj01", text, StringComparison.Ordinal);
        Assert.Contains("Obj40", text, StringComparison.Ordinal);
        Assert.Contains("40 objects", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_that_cannot_be_trusted_is_explained_without_a_schema_number()
    {
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [Applied("App/A.st")],
            StructuralChangesUnavailable = "the manifest is schema 1, below 4 — what it does "
                                           + "not list cannot be told apart from …",
        });

        // The internal sentence names a schema number, which answers a question nobody
        // standing in front of this dialog asked.
        Assert.DoesNotContain("schema", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content is still applied", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_planners_own_warnings_reach_the_person_deciding()
    {
        // ImportPlan.Warnings is documented as "sentences a person should see before or after
        // the import runs". Before is this dialog; until now the only "before" was the log,
        // which is where nobody is looking when the question is "should I press Import".
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [Applied("App/A.st")],
            Warnings = ["README.md is not something this version can import — skipped"],
        });

        Assert.Contains("README.md", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_that_could_not_be_computed_says_nothing_rather_than_apologising()
    {
        // Null means the plan could not be built — an unknown binding, or no format claiming
        // the project — and the import refuses or replans on its own. Explaining that here
        // would put a mechanism in front of somebody who asked about their project.
        Assert.Equal(string.Empty, ImportNarrative.Describe(null));

        // An empty plan is different from no plan, and equally quiet: there is nothing to add
        // to the sentence the caller already wrote.
        Assert.Equal(string.Empty, ImportNarrative.Describe(new ImportPlan { Files = [] }));
    }
}
