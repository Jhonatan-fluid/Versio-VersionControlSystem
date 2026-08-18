using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// How a library reference row presents, and when the manager holding it opens itself.
/// </summary>
/// <remarks>
/// The builder's rules are tested in Core; this covers the seam the view binds to — the
/// annotation, the file a click opens, and the expansion default, which is the one place
/// where "an object owns rows now" had not yet been carried through: the folder-only rule
/// left a bumped library version behind a closed chevron.
/// </remarks>
public sealed class TreeRowLibraryTests
{
    private static ProjectTreeNode Library(ChangeKind status = ChangeKind.Unmodified) => new()
    {
        Name = "Standard",
        Path = "plc/M/Device/App/Library Manager.libraries.yaml::#Standard",
        Kind = ProjectNodeKind.Library,
        Detail = "Standard, 3.5.22.0 (System)",
        Files = ["plc/M/Device/App/Library Manager.libraries.yaml"],
        Status = status,
    };

    private static ProjectTreeNode Manager(params ProjectTreeNode[] references) => new()
    {
        Name = "Library Manager",
        Path = "plc/M/Device/App/Library Manager.libraries.yaml",
        Kind = ProjectNodeKind.Object,
        Type = "LIBRARY_MANAGER",
        Files = ["plc/M/Device/App/Library Manager.libraries.yaml"],
        Children = references,
        ChangedDescendants = references.Count(r => r.Status != ChangeKind.Unmodified),
    };

    [Fact]
    public void A_library_row_says_its_version_where_an_object_says_its_type()
    {
        var row = new TreeRow(Library(), depth: 3);

        Assert.Equal("Standard, 3.5.22.0 (System)", row.Annotation);
    }

    [Fact]
    public void Clicking_a_library_opens_the_managers_file()
    {
        // The reference has no file of its own; the YAML is where its facts are recorded,
        // and a row whose click opened nothing would read as a rendering defect.
        var row = new TreeRow(Library(), depth: 3);

        Assert.Equal("plc/M/Device/App/Library Manager.libraries.yaml", row.PrimaryFile);
    }

    [Fact]
    public void A_manager_with_a_bumped_library_opens_itself()
    {
        // The version bump is the change under review; behind a closed chevron only the
        // badge hints at it. Same rule the folders already follow, extended to any node
        // with children now that objects own rows.
        var expansion = new TreeExpansion();
        var row = new TreeRow(Manager(Library(ChangeKind.Modified)), depth: 2, expansion);

        Assert.True(row.IsExpanded);
    }

    [Fact]
    public void A_quiet_manager_stays_closed()
    {
        // Opening everything would bury the four objects somebody edited under the four
        // thousand they did not — the reason the default is change-driven at all.
        var expansion = new TreeExpansion();
        var row = new TreeRow(Manager(Library()), depth: 2, expansion);

        Assert.False(row.IsExpanded);
    }

    [Fact]
    public void A_manager_the_user_closed_stays_closed_over_the_default()
    {
        // The user's click outranks the rule, or every refresh reopens what they just shut.
        var expansion = new TreeExpansion();
        var node = Manager(Library(ChangeKind.Modified));
        expansion.Record(node, expanded: false);

        Assert.False(new TreeRow(node, depth: 2, expansion).IsExpanded);
    }
}
