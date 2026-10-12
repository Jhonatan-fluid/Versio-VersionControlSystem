using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// Which layer of an object a pane opens: the readable one, whichever order the export listed
/// them in. The desktop and the web viewer both ask this, so they open the same file.
/// </summary>
public sealed class PrimaryFileTests
{
    private static ProjectTreeNode Node(ProjectNodeKind kind, params string[] files) =>
        new() { Name = "X", Path = "X", Kind = kind, Files = files };

    // A vendor's own text extensions (.scl, .udt) win through its format: SecondVendorTests here,
    // TiaPortalFormatTests for the real ones. Only the standard's .st is Core's to know.
    [Theory]
    [InlineData("Program/Main.xml", "Program/Main.st", "Program/Main.st")]
    [InlineData("Program/Main.st", "Program/Main.xml", "Program/Main.st")]
    public void The_readable_layer_wins_over_the_native_one(string first, string second, string expected)
    {
        Assert.Equal(expected, Node(ProjectNodeKind.Object, first, second).PrimaryFile);
    }

    [Fact]
    public void With_no_readable_layer_the_first_file_opens()
    {
        Assert.Equal("Device/Task.xml", Node(ProjectNodeKind.Object, "Device/Task.xml", "Device/Task.json").PrimaryFile);
        Assert.Equal("README.md", Node(ProjectNodeKind.File, "README.md").PrimaryFile);
    }

    [Fact]
    public void A_project_or_a_folder_opens_nothing()
    {
        Assert.Null(Node(ProjectNodeKind.Project, "Line3.project").PrimaryFile);
        Assert.Null(Node(ProjectNodeKind.Folder, "a.st").PrimaryFile);
    }
}
