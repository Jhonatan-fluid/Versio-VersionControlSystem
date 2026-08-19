using Versio.Core.Bindings;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Bindings;

public class BindingConventionsTests
{
    [Fact]
    public void Export_path_is_the_project_name_as_a_sibling_folder()
    {
        Assert.Equal("plc/MyMachine",
            BindingConventions.ConventionalExportPath("plc/MyMachine.project"));
    }

    [Fact]
    public void Template_path_follows_the_naming_convention()
    {
        Assert.Equal("plc/MyMachine_template.project",
            BindingConventions.ConventionalTemplatePath("plc/MyMachine.project"));
    }

    [Theory]
    [InlineData("plc/MyMachine_template.project", true)]
    [InlineData(@"plc\MyMachine_template.project", true)]
    [InlineData("plc/MyMachine_TEMPLATE.project", true)]
    [InlineData("plc/MyMachine.project", false)]
    [InlineData("plc/template.project", false)]
    public void Templates_are_recognised_by_name(string path, bool expected)
    {
        Assert.Equal(expected, BindingConventions.LooksLikeTemplate(path));
    }
}

public class OrphanScannerTests
{
    private static readonly OrphanScanner Scanner = new();

    private static ProjectBinding Binding(string id, string project) => new()
    {
        Id = id,
        ProjectPath = project,
        TemplatePath = BindingConventions.ConventionalTemplatePath(project),
        ExportPath = BindingConventions.ConventionalExportPath(project),
    };

    [Fact]
    public void A_healthy_binding_reports_Bound()
    {
        var config = new RepoConfig { Bindings = [Binding("b-1", "plc/A.project")] };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["plc/A.project"],
            ExportDirectories = ["plc/A"],
            ManifestBindingIds = new Dictionary<string, string> { ["plc/A"] = "b-1" },
        };

        var findings = Scanner.Scan(config, snapshot);

        Assert.Equal(BindingHealth.Bound, findings.Single().Health);
        Assert.DoesNotContain(findings, f => f.NeedsAttention);
    }

    [Fact]
    public void An_untracked_project_is_offered_for_adoption()
    {
        // Without this, someone adds a project, nothing happens, and they reasonably
        // conclude the tool is broken.
        var findings = Scanner.Scan(new RepoConfig(), new RepoSnapshot
        {
            ProjectFiles = ["plc/New.project"],
        });

        var f = Assert.Single(findings);
        Assert.Equal(BindingHealth.OrphanedProject, f.Health);
        Assert.Equal("plc/New.project", f.Path);

        // The sentence, which nothing asserted until 2026-08-16 — so it read "untracked CODESYS
        // project" for as long as it liked, in a namespace forbidden to name a vendor, and could
        // have drifted in any direction without a test noticing. Scan works from a snapshot and
        // is handed no format, so "PLC" is the most it can honestly say.
        Assert.Contains("untracked PLC project", f.Explanation);
        Assert.DoesNotContain("CODESYS", f.Explanation);
    }

    [Fact]
    public void A_TEMPLATE_is_never_offered_for_adoption()
    {
        // A template IS a .project file. Adopting it would create a binding that exports
        // the template into its own tree - nonsense, and confusing to unpick. (R28)
        var findings = Scanner.Scan(new RepoConfig(), new RepoSnapshot
        {
            ProjectFiles = ["plc/A_template.project"],
        });

        Assert.Empty(findings);
    }

    [Fact]
    public void A_bound_template_is_never_offered_for_adoption_either()
    {
        // Second, independent guard: a template referenced by a binding, whatever it is named.
        var binding = Binding("b-1", "plc/A.project") with { TemplatePath = "plc/base.project" };
        var config = new RepoConfig { Bindings = [binding] };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["plc/A.project", "plc/base.project"],
            ExportDirectories = ["plc/A"],
        };

        var findings = Scanner.Scan(config, snapshot);

        Assert.DoesNotContain(findings, f => f.Health == BindingHealth.OrphanedProject);
    }

    [Fact]
    public void Ignored_paths_are_not_offered()
    {
        var config = new RepoConfig { Ignore = ["samples/**", "archive/**"] };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["samples/Demo.project", "archive/Old.project", "plc/Real.project"],
        };

        var findings = Scanner.Scan(config, snapshot);

        var orphan = Assert.Single(findings, f => f.Health == BindingHealth.OrphanedProject);
        Assert.Equal("plc/Real.project", orphan.Path);
    }

    [Fact]
    public void An_ignored_paths_export_tree_is_not_offered_either()
    {
        // HALF A RULE IS WORSE THAN NONE HERE. The ignore list filtered orphaned projects and
        // not their mirrors, so ignoring a directory of scratch projects made the projects
        // disappear from the panel and left one finding per export tree — and a scratch
        // directory of round-trip experiments has an export tree for every project in it.
        //
        // Found by opening this repository in the app: adoption correctly ignored 21 phase0
        // projects and the panel still said 43 things needed attention. Nothing in the suite
        // noticed, because the existing test only ever supplied ProjectFiles.
        var config = new RepoConfig { Ignore = ["scratch/**"] };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["scratch/Probe.project", "plc/Real.project"],
            ExportDirectories = ["scratch/Probe", "scratch/RoundTrip", "plc/Orphaned"],
        };

        var findings = Scanner.Scan(config, snapshot);

        // The one export tree outside the ignored directory, and only that one.
        var orphan = Assert.Single(findings, f => f.Health == BindingHealth.OrphanedExport);
        Assert.Equal("plc/Orphaned", orphan.Path);

        Assert.DoesNotContain(findings, f => f.Path.StartsWith("scratch/", StringComparison.Ordinal));
    }

    [Fact]
    public void An_export_tree_with_no_project_is_reported_but_never_deleted()
    {
        var findings = Scanner.Scan(new RepoConfig(), new RepoSnapshot
        {
            ExportDirectories = ["plc/Gone"],
        });

        var f = Assert.Single(findings);
        Assert.Equal(BindingHealth.OrphanedExport, f.Health);

        // The explanation must not CLAIM the tree is committed history — the scan cannot
        // know, and for an export tree under an ignored path it was simply false. "May be"
        // is the most the snapshot can honestly say.
        Assert.Contains("never deleted automatically", f.Explanation);
        Assert.Contains("may be committed history", f.Explanation);
    }

    [Fact]
    public void A_binding_pointing_at_a_missing_project_is_reported_not_skipped()
    {
        var config = new RepoConfig { Bindings = [Binding("b-1", "plc/Gone.project")] };

        var findings = Scanner.Scan(config, new RepoSnapshot());

        var f = Assert.Single(findings, x => x.BindingId == "b-1");
        Assert.Equal(BindingHealth.BrokenBinding, f.Health);
    }

    [Fact]
    public void A_manifest_claiming_a_different_binding_is_a_mismatch_not_a_guess()
    {
        // Two-way handshake: repo.json points forward, the manifest points back. A
        // disagreement must stop the pipeline rather than be papered over.
        var config = new RepoConfig { Bindings = [Binding("b-1", "plc/A.project")] };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["plc/A.project"],
            ExportDirectories = ["plc/A"],
            ManifestBindingIds = new Dictionary<string, string> { ["plc/A"] = "b-SOMETHING-ELSE" },
        };

        var f = Assert.Single(Scanner.Scan(config, snapshot));

        Assert.Equal(BindingHealth.ManifestMismatch, f.Health);
    }

    [Fact]
    public void A_casing_only_difference_gets_its_own_diagnosis()
    {
        // Windows sees one file, git sees two index entries. Reporting this as "missing"
        // would send someone hunting for a file that is right there.
        var config = new RepoConfig { Bindings = [Binding("b-1", "plc/MyMachine.project")] };
        var snapshot = new RepoSnapshot { ProjectFiles = ["plc/mymachine.project"] };

        var f = Assert.Single(Scanner.Scan(config, snapshot));

        Assert.Equal(BindingHealth.CasingMismatch, f.Health);
    }

    [Fact]
    public void A_git_detected_rename_re_links_the_binding_instead_of_orphaning_it()
    {
        // Identity is the binding id, never the path. Without this the diff would show the
        // entire project as newly added and the baseline would reset. (R29)
        var config = new RepoConfig { Bindings = [Binding("b-1", "plc/Old.project")] };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["plc/New.project"],
            GitRenames = new Dictionary<string, string> { ["plc/Old.project"] = "plc/New.project" },
        };

        var findings = Scanner.Scan(config, snapshot);

        var bound = Assert.Single(findings, f => f.Health == BindingHealth.Bound);
        Assert.Equal("b-1", bound.BindingId);
        Assert.Equal("plc/New.project", bound.Path);
        Assert.DoesNotContain(findings, f => f.Health == BindingHealth.OrphanedProject);
    }

    [Fact]
    public void Several_projects_in_one_repo_are_each_tracked_independently()
    {
        var config = new RepoConfig
        {
            Bindings = [Binding("b-1", "plc/A.project"), Binding("b-2", "plc/B.project")],
        };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["plc/A.project", "plc/B.project", "plc/C.project"],
            ExportDirectories = ["plc/A", "plc/B"],
        };

        var findings = Scanner.Scan(config, snapshot);

        Assert.Equal(2, findings.Count(f => f.Health == BindingHealth.Bound));
        Assert.Equal("plc/C.project",
            Assert.Single(findings, f => f.Health == BindingHealth.OrphanedProject).Path);
    }

    [Fact]
    public void Disabled_bindings_are_left_alone()
    {
        var config = new RepoConfig
        {
            Bindings = [Binding("b-1", "plc/A.project") with { Enabled = false }],
        };

        var findings = Scanner.Scan(config, new RepoSnapshot());

        Assert.Empty(findings);
    }

    [Fact]
    public void Backslash_paths_are_normalized_before_comparison()
    {
        var config = new RepoConfig { Bindings = [Binding("b-1", @"plc\A.project")] };
        var snapshot = new RepoSnapshot
        {
            ProjectFiles = ["plc/A.project"],
            ExportDirectories = ["plc/A"],
        };

        Assert.Equal(BindingHealth.Bound, Scanner.Scan(config, snapshot).Single().Health);
    }

    [Fact]
    public void A_proposed_binding_uses_the_conventions()
    {
        var b = OrphanScanner.ProposeBinding("plc/New.project", "b-new", "CODESYS V3.5 SP22 Patch 2");

        Assert.Equal("plc/New.project", b.ProjectPath);
        Assert.Equal("plc/New", b.ExportPath);
        Assert.Equal("plc/New_template.project", b.TemplatePath);
        Assert.Equal("CODESYS V3.5 SP22 Patch 2", b.Profile);
        Assert.True(b.Vendor.IsUnknown);
        Assert.True(b.Enabled);
    }

    [Fact]
    public void A_format_template_is_not_offered_for_adoption()
    {
        // .alXX archives are this vendor's packed project. The scanner used to only know
        // *_template.project, so an archive looked like an ordinary orphan.
        var scanner = new OrphanScanner(path =>
            path.EndsWith(".al21", StringComparison.OrdinalIgnoreCase)
                ? ArchiveFormat.Instance
                : null);

        var findings = scanner.Scan(new RepoConfig(), new RepoSnapshot
        {
            ProjectFiles = ["plc/Archive.al21"],
        });

        Assert.Empty(findings);
    }

    private sealed class ArchiveFormat : IPlcProjectFormat
    {
        public static readonly ArchiveFormat Instance = new();

        public PlcVendor Vendor => new("ArchiveStandIn");
        public string DisplayName => "archive stand-in";
        public PlcCapabilities Capabilities => new();
        public IReadOnlyList<string> ProjectExtensions => [".al21"];
        public bool IsTemplate(string path) =>
            path.EndsWith(".al21", StringComparison.OrdinalIgnoreCase);
        public string ConventionalExportPath(string projectPath) => projectPath + ".export";
        public string? OccupancyMarkerFor(string projectPath) => null;
        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];
        public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;
        public bool NeedsTextToPlan(string relativePath) => false;
        public ImportPlan PlanImport(
            Versio.Core.Projects.ProjectManifest? manifest,
            IReadOnlyList<MirrorFile> mirror,
            IReadOnlyDictionary<string, string> departed) =>
            new() { Files = [] };
        public Versio.Core.Projects.ObjectTypeName? DisplayNameFor(string type) => null;
    }
}
