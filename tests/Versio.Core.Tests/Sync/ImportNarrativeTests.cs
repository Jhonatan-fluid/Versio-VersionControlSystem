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

    private static PlannedDeletion Gone(string name) => new()
    {
        Guid = name, Name = name,
        Files = [new DeletedFile($"App/{name}.st", "sha256:0", "ST_FULL")],
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

    [Fact]
    public void Objects_with_no_exported_file_are_named_and_said_to_be_staying()
    {
        // PRESENT TENSE, and this is the assertion that keeps it honest. Deletions are planned
        // and executed by nothing, so a dialog promising removal would be the same lie as the
        // one this whole chain exists to prevent, pointed the other way. When the script grows
        // remove(), this test is what says the wording has to change with it.
        var text = ImportNarrative.Describe(new ImportPlan
        {
            Files = [Applied("App/A.st")],
            Deletions = [Gone("FB_Motor"), Gone("Signals")],
        });

        Assert.Contains("FB_Motor, Signals", text, StringComparison.Ordinal);
        Assert.Contains("they will stay", text, StringComparison.Ordinal);
        Assert.DoesNotContain("will be removed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("will be deleted", text, StringComparison.Ordinal);
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
