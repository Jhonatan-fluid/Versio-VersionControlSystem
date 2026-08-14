using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The changed files, said the way an engineer would say them.
/// </summary>
/// <remarks>
/// Two rules here are worth more than the wording they produce. <b>Objects are counted, not
/// files</b> — a GVL is two files and one edit, and every count taken over paths reports it
/// twice. And <b>the drafted subject drops whole names</b>: half of <c>FB_MotorControl</c> is
/// not a shorter version of it, it is <c>FB_Motor</c>, which may well be a different function
/// block in the same project.
/// </remarks>
public sealed class ChangeSummaryTests
{
    private const string Root = "plc/Machine";

    private static ProjectObject Obj(string path, string name, string type, string guid,
        string form = "ST_FULL") =>
        new() { Path = path, Name = name, Type = type, Guid = guid, Form = form };

    private static ObjectIndex Index(params ProjectObject[] objects) =>
        ObjectIndex.Of([
            new WorkspaceProject
            {
                ProjectPath = "plc/Machine.project",
                ExportPath = Root,
                Manifest = new ProjectManifest
                {
                    Schema = 4,
                    Project = "Machine.project",
                    Objects = objects,
                },
            },
        ]);

    private static FileChange Changed(string path, ChangeKind kind = ChangeKind.Modified) =>
        new() { Path = path, Staged = ChangeKind.Unmodified, Unstaged = kind };

    // ---- the sentence -----------------------------------------------------------------

    [Fact]
    public void The_sentence_counts_by_type_and_names_the_project()
    {
        var index = Index(
            Obj("App/FB_A.st", "FB_A", "POU", "p1"),
            Obj("App/FB_B.st", "FB_B", "POU", "p2"),
            Obj("App/FB_C.st", "FB_C", "POU", "p3"),
            Obj("App/Glob.gvl.st", "Glob", "GVL", "g1", "GVL_BOTH"),
            Obj("App/Recipe.dut.st", "Recipe", "DUT", "d1", "ST_DECL"));

        var sentence = ChangeSummary.Sentence(
            [
                Changed($"{Root}/App/FB_A.st"),
                Changed($"{Root}/App/FB_B.st"),
                Changed($"{Root}/App/FB_C.st"),
                Changed($"{Root}/App/Glob.gvl.st"),
                Changed($"{Root}/App/Recipe.dut.st"),
            ],
            index);

        // Most first, then alphabetically. The tie-break has to be something, and a stable
        // order matters more than which one: a sentence that reshuffled its middle two clauses
        // between refreshes would read as the counts having changed.
        Assert.Equal("3 POUs, 1 DUT and 1 GVL changed in Machine", sentence);
    }

    [Fact]
    public void Two_files_of_one_object_are_one_object()
    {
        var index = Index(
            Obj("App/Glob.gvl.st", "Glob", "GVL", "g1", "GVL_BOTH"),
            Obj("App/Glob.gvl.xml", "Glob", "GVL", "g1", "GVL_BOTH"));

        var sentence = ChangeSummary.Sentence(
            [Changed($"{Root}/App/Glob.gvl.st"), Changed($"{Root}/App/Glob.gvl.xml")],
            index);

        Assert.Equal("1 GVL changed in Machine", sentence);
    }

    [Fact]
    public void A_file_that_is_not_an_object_is_counted_apart_and_never_named_as_one()
    {
        var index = Index(Obj("App/FB_A.st", "FB_A", "POU", "p1"));

        var sentence = ChangeSummary.Sentence(
            [Changed($"{Root}/App/FB_A.st"), Changed("README.md"), Changed("build/ci.yml")],
            index);

        Assert.Equal("1 POU changed in Machine, plus 2 other files", sentence);
    }

    [Fact]
    public void Nothing_that_belongs_to_a_project_says_nothing()
    {
        var index = Index(Obj("App/FB_A.st", "FB_A", "POU", "p1"));

        Assert.Equal(string.Empty, ChangeSummary.Sentence([Changed("README.md")], index));
        Assert.Equal(string.Empty, ChangeSummary.Sentence([], index));
        Assert.Equal(string.Empty, ChangeSummary.Sentence([Changed("a.st")], ObjectIndex.Empty));
    }

    [Fact]
    public void Objects_in_several_projects_are_counted_together_and_the_projects_are_counted()
    {
        var index = ObjectIndex.Of([
            new WorkspaceProject
            {
                ProjectPath = "a/One.project",
                ExportPath = "a/One",
                Manifest = new ProjectManifest
                {
                    Schema = 4,
                    Project = "One.project",
                    Objects = [Obj("Main.st", "Main", "POU", "g1")],
                },
            },
            new WorkspaceProject
            {
                ProjectPath = "b/Two.project",
                ExportPath = "b/Two",
                Manifest = new ProjectManifest
                {
                    Schema = 4,
                    Project = "Two.project",
                    Objects = [Obj("Main.st", "Main", "POU", "g2")],
                },
            },
        ]);

        Assert.Equal(
            "2 POUs changed in 2 projects",
            ChangeSummary.Sentence([Changed("a/One/Main.st"), Changed("b/Two/Main.st")], index));
    }

    // ---- the drafted subject ----------------------------------------------------------

    [Fact]
    public void The_draft_groups_by_verb_with_modify_first()
    {
        var index = Index(
            Obj("App/FB_Motor.st", "FB_Motor", "POU", "p1"),
            Obj("App/GVL_Alarms.gvl.st", "GVL_Alarms", "GVL", "g1", "GVL_BOTH"),
            Obj("App/DUT_Recipe.dut.st", "DUT_Recipe", "DUT", "d1", "ST_DECL"));

        var subject = ChangeSummary.DraftSubject(
            [
                Changed($"{Root}/App/DUT_Recipe.dut.st", ChangeKind.Added),
                Changed($"{Root}/App/FB_Motor.st"),
                Changed($"{Root}/App/GVL_Alarms.gvl.st"),
            ],
            index);

        Assert.Equal("Modify FB_Motor, GVL_Alarms; add DUT_Recipe", subject);
        Assert.True(subject.Length <= ChangeSummary.SubjectLimit);
    }

    [Fact]
    public void A_subject_over_the_limit_drops_whole_names_and_counts_them()
    {
        var index = Index(
            Obj("App/FB_Conveyor.st", "FB_Conveyor", "POU", "p1"),
            Obj("App/FB_Compressor.st", "FB_Compressor", "POU", "p2"),
            Obj("App/FB_Evaporator.st", "FB_Evaporator", "POU", "p3"),
            Obj("App/FB_Thermostat.st", "FB_Thermostat", "POU", "p4"),
            Obj("App/FB_Defroster.st", "FB_Defroster", "POU", "p5"));

        var subject = ChangeSummary.DraftSubject(
            [
                Changed($"{Root}/App/FB_Conveyor.st"),
                Changed($"{Root}/App/FB_Compressor.st"),
                Changed($"{Root}/App/FB_Evaporator.st"),
                Changed($"{Root}/App/FB_Thermostat.st"),
                Changed($"{Root}/App/FB_Defroster.st"),
            ],
            index);

        Assert.True(subject.Length <= ChangeSummary.SubjectLimit,
            $"subject was {subject.Length} characters: {subject}");
        Assert.EndsWith("more", subject);

        // Whole names or nothing: every name that survived is one that exists in the project.
        foreach (var name in subject
                     .Replace("Modify ", string.Empty)
                     .Split([",", " and "], StringSplitOptions.TrimEntries)
                     .Where(p => !p.EndsWith("more", StringComparison.Ordinal)))
        {
            Assert.Contains(name, (string[])
                ["FB_Conveyor", "FB_Compressor", "FB_Evaporator", "FB_Thermostat", "FB_Defroster"]);
        }
    }

    [Fact]
    public void One_object_with_a_very_long_name_is_shown_whole()
    {
        var name = new string('X', 70);
        var index = Index(Obj("App/Long.st", name, "POU", "p1"));

        var subject = ChangeSummary.DraftSubject([Changed($"{Root}/App/Long.st")], index);

        // Over the limit deliberately: there is no honest shorter form, and the counter beside
        // the box already says it is long. Truncating would invent a name.
        Assert.Equal($"Modify {name}", subject);
    }

    [Fact]
    public void An_object_whose_files_disagree_is_modified_not_deleted()
    {
        var index = Index(
            Obj("App/Glob.gvl.st", "Glob", "GVL", "g1", "GVL_BOTH"),
            Obj("App/Glob.gvl.xml", "Glob", "GVL", "g1", "GVL_BOTH"));

        var subject = ChangeSummary.DraftSubject(
            [
                Changed($"{Root}/App/Glob.gvl.st", ChangeKind.Deleted),
                Changed($"{Root}/App/Glob.gvl.xml", ChangeKind.Modified),
            ],
            index);

        Assert.Equal("Modify Glob", subject);
    }

    [Theory]
    [InlineData(ChangeKind.Added, "Add FB_A")]
    [InlineData(ChangeKind.Untracked, "Add FB_A")]
    [InlineData(ChangeKind.Deleted, "Delete FB_A")]
    [InlineData(ChangeKind.Renamed, "Rename FB_A")]
    [InlineData(ChangeKind.Modified, "Modify FB_A")]
    [InlineData(ChangeKind.Conflicted, "Modify FB_A")]
    public void Each_kind_of_change_gets_the_word_an_engineer_would_have_typed(
        ChangeKind kind, string expected)
    {
        var index = Index(Obj("App/FB_A.st", "FB_A", "POU", "p1"));

        Assert.Equal(expected,
            ChangeSummary.DraftSubject([Changed($"{Root}/App/FB_A.st", kind)], index));
    }

    [Fact]
    public void Nothing_to_describe_drafts_nothing()
    {
        var index = Index(Obj("App/FB_A.st", "FB_A", "POU", "p1"));

        Assert.Equal(string.Empty, ChangeSummary.DraftSubject([Changed("README.md")], index));
        Assert.Equal(string.Empty, ChangeSummary.DraftSubject([], index));
    }

    [Fact]
    public void An_export_only_file_is_still_described_it_is_just_flagged_elsewhere()
    {
        var index = Index(Obj("App/Library Manager.libraries.yaml", "Library Manager",
            "LIBRARY_MANAGER", "l1", ExportCoverage.ExportOnlyForm));

        var changes = new[] { Changed($"{Root}/App/Library Manager.libraries.yaml") };

        Assert.Equal("1 library manager changed in Machine",
            ChangeSummary.Sentence(changes, index));
        Assert.Equal("Modify Library Manager", ChangeSummary.DraftSubject(changes, index));
        Assert.True(ChangeSummary.Objects(changes, index)[0].Object.IsExportOnly);
    }
}
