using Versio.Core.Bindings;
using Versio.Core.Plc;
using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Bindings;

/// <summary>
/// The guards on adopting a project.
/// </summary>
/// <remarks>
/// Adoption writes a mapping into <c>repo.json</c> that every teammate pulls, and the export
/// that follows writes a whole directory tree. Each refusal below is a state that would
/// otherwise produce a binding somebody has to work out how to unpick.
/// </remarks>
public sealed class AdoptionTests
{
    private static readonly IPlcProjectFormat Format = CodesysProjectFormat.Instance;

    private static AdoptionDecision Plan(string path, RepoConfig? config = null) =>
        Adoption.Plan(path, config ?? new RepoConfig(), Format, "b-1", "CODESYS V3.5 SP22 Patch 2");

    [Fact]
    public void An_untracked_project_can_be_adopted()
    {
        var decision = Plan("plc/Machine.project");

        Assert.Equal(AdoptionVerdict.Ready, decision.Verdict);
        Assert.True(decision.CanAdopt);
        Assert.Equal("plc/Machine.project", decision.Binding!.ProjectPath);
        Assert.Equal("plc/Machine", decision.Binding.ExportPath);
        Assert.Equal("plc/Machine_template.project", decision.Binding.TemplatePath);
        Assert.Equal("CODESYS V3.5 SP22 Patch 2", decision.Binding.Profile);
        Assert.True(decision.Binding.Enabled);
    }

    [Fact]
    public void The_binding_id_is_the_one_supplied_not_one_derived_from_the_path()
    {
        // Identity has to survive a rename, so it can never be a function of a path.
        var decision = Adoption.Plan("plc/Machine.project", new RepoConfig(), Format, "chosen-id");

        Assert.Equal("chosen-id", decision.Binding!.Id);
    }

    [Fact]
    public void Backslashes_are_normalised_so_the_same_file_is_never_adopted_twice()
    {
        var config = Bound("plc/Machine.project");

        var decision = Plan(@"plc\Machine.project", config);

        Assert.Equal(AdoptionVerdict.AlreadyBound, decision.Verdict);
    }

    [Fact]
    public void A_template_is_refused()
    {
        // R28: a binding that exports a template into its own tree is nonsense, and
        // genuinely confusing to unpick afterwards.
        var decision = Plan("plc/Machine_template.project");

        Assert.Equal(AdoptionVerdict.IsTemplate, decision.Verdict);
        Assert.Null(decision.Binding);
    }

    [Fact]
    public void A_file_another_binding_uses_as_its_template_is_refused()
    {
        var config = new RepoConfig
        {
            Bindings =
            [
                new ProjectBinding
                {
                    Id = "b-other",
                    ProjectPath = "plc/Machine.project",
                    ExportPath = "plc/Machine",
                    TemplatePath = "plc/Seed.project",
                },
            ],
        };

        var decision = Plan("plc/Seed.project", config);

        Assert.Equal(AdoptionVerdict.BoundAsTemplate, decision.Verdict);
    }

    [Fact]
    public void An_already_tracked_project_is_refused()
    {
        var decision = Plan("plc/Machine.project", Bound("plc/Machine.project"));

        Assert.Equal(AdoptionVerdict.AlreadyBound, decision.Verdict);
        Assert.Contains("b-existing", decision.Explanation);
    }

    [Fact]
    public void A_disabled_binding_still_owns_its_project()
    {
        // Adopting around a disabled binding would leave two entries competing for one
        // export directory, and the explanation has to point at re-enabling instead.
        var config = Bound("plc/Machine.project") with
        {
            Bindings = [Bound("plc/Machine.project").Bindings[0] with { Enabled = false }],
        };

        var decision = Plan("plc/Machine.project", config);

        Assert.Equal(AdoptionVerdict.AlreadyBound, decision.Verdict);
        Assert.Contains("re-enable", decision.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_project_whose_export_directory_is_taken_is_refused()
    {
        // Two projects exporting into one tree overwrite each other silently — the failure
        // mode that produces a diff nobody can explain.
        var config = new RepoConfig
        {
            Bindings =
            [
                new ProjectBinding
                {
                    Id = "b-other",
                    ProjectPath = "old/Machine.project",
                    ExportPath = "plc/Machine",
                },
            ],
        };

        var decision = Plan("plc/Machine.project", config);

        Assert.Equal(AdoptionVerdict.ExportPathTaken, decision.Verdict);
        Assert.Contains("b-other", decision.Explanation);
    }

    [Theory]
    // The reviewer's case, in both orders: A.project exports to A, and A/B.project to A/B.
    // An export replaces its whole directory, so exporting A deletes A/B.project and A/B.
    [InlineData("plc/A/B.project", "plc/A/B", "plc/A.project")]
    [InlineData("old/A.project", "plc/A", "plc/A/B.project")]
    // The other binding's project file inside the new export root, with its own export
    // elsewhere.
    [InlineData("plc/A/Other.project", "mirrors/Other", "plc/A.project")]
    public void A_project_whose_export_directory_would_nest_another_binding_s_is_refused(
        string existingProject, string existingExport, string newProject)
    {
        // Review 2026-10-25, fifth round, item 2: the collision check compared export
        // directories for equality only, so one nested in another was accepted and the
        // next export of the outer project deleted the inner one, project file and all.
        var config = new RepoConfig
        {
            Bindings =
            [
                new ProjectBinding
                {
                    Id = "b-other",
                    ProjectPath = existingProject,
                    ExportPath = existingExport,
                },
            ],
        };

        var decision = Plan(newProject, config);

        Assert.Equal(AdoptionVerdict.ExportPathTaken, decision.Verdict);
        Assert.Contains("b-other", decision.Explanation);
        Assert.Contains("delete", decision.Explanation);
    }

    [Theory]
    [InlineData("plc/A/B.project", false)]
    [InlineData("plc/A/Deep/C.fbsproj", false)]
    [InlineData("plc/AB.project", true)]
    [InlineData("other/D.project", true)]
    public void A_project_whose_export_directory_would_contain_an_untracked_project_is_refused(
        string discovered, bool ready)
    {
        // Review 2026-10-25, sixth round, item 1: the fifth round's check read the bindings,
        // and on a fresh scan there are none — A.project was adopted first, A/B.project was then
        // refused, and A's export deleted it anyway. The scan's projects count, tracked or not.
        var decision = Adoption.Plan(
            "plc/A.project", new RepoConfig(), Format, "b1",
            otherProjects: ["plc/A.project", discovered]);

        if (ready)
        {
            Assert.Equal(AdoptionVerdict.Ready, decision.Verdict);
        }
        else
        {
            Assert.Equal(AdoptionVerdict.ExportPathTaken, decision.Verdict);
            Assert.Contains(discovered, decision.Explanation);
            Assert.Contains("not tracked", decision.Explanation);
        }
    }

    [Fact]
    public void A_sibling_whose_name_merely_starts_the_same_way_is_not_nested()
    {
        // plc/AB is not inside plc/A: the boundary is a directory separator, not a prefix.
        var config = new RepoConfig
        {
            Bindings =
            [
                new ProjectBinding
                {
                    Id = "b-other", ProjectPath = "old/AB.project", ExportPath = "plc/AB",
                },
            ],
        };

        Assert.Equal(AdoptionVerdict.Ready, Plan("plc/A.project", config).Verdict);
    }

    [Fact]
    public void A_non_project_file_is_refused()
    {
        var decision = Plan("plc/notes.txt");

        Assert.Equal(AdoptionVerdict.NotAProject, decision.Verdict);
    }

    [Fact]
    public void A_library_is_a_project_for_this_vendor()
    {
        var decision = Plan("libs/Motion.library");

        Assert.Equal(AdoptionVerdict.Ready, decision.Verdict);
        Assert.Equal("libs/Motion.library.export", decision.Binding!.ExportPath);
    }

    [Theory]
    [InlineData(@"C:\GIT\repo\plc\Machine.project")]
    [InlineData("/srv/repo/plc/Machine.project")]
    public void An_absolute_path_is_refused(string path)
    {
        // A rooted path survives one session and breaks on the next clone. The orchestrator
        // would use it quite happily, which is exactly why it is stopped here.
        var decision = Plan(path);

        Assert.Equal(AdoptionVerdict.NotRepoRelative, decision.Verdict);
    }

    [Fact]
    public void An_ignored_path_is_refused_so_the_list_and_the_button_agree()
    {
        var config = new RepoConfig { Ignore = ["vendor/**"] };

        var decision = Plan("vendor/Sample.project", config);

        Assert.Equal(AdoptionVerdict.Ignored, decision.Verdict);
    }

    [Fact]
    public void Every_refusal_explains_itself()
    {
        // A greyed-out button with no reason is the failure this enum exists to prevent.
        string[] paths =
        [
            "plc/Machine_template.project", "plc/notes.txt", @"C:\x\Machine.project", "",
        ];

        foreach (var path in paths)
        {
            var decision = Plan(path);
            Assert.False(decision.CanAdopt);
            Assert.False(string.IsNullOrWhiteSpace(decision.Explanation));
        }
    }

    private static RepoConfig Bound(string projectPath) => new()
    {
        Bindings =
        [
            new ProjectBinding
            {
                Id = "b-existing",
                ProjectPath = projectPath,
                ExportPath = BindingConventions.ConventionalExportPath(projectPath),
            },
        ],
    };
}
