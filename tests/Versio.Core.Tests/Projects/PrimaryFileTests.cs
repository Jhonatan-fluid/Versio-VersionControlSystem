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

    [Theory]
    [InlineData("Program/Main.xml", "Program/Main.st", "Program/Main.st")]
    [InlineData("Blocks/DB_Recipe.xml", "Blocks/DB_Recipe.scl", "Blocks/DB_Recipe.scl")]
    [InlineData("Types/UDT_Valve.xml", "Types/UDT_Valve.udt", "Types/UDT_Valve.udt")]
    public void The_readable_layer_wins_over_the_native_one(string native, string readable, string expected)
    {
        Assert.Equal(expected, Node(ProjectNodeKind.Object, native, readable).PrimaryFile);
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
