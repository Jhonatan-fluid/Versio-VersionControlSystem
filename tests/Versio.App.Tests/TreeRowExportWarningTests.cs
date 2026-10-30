using Versio.App.ViewModels;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// An object the last export could not write says so on its row.
/// </summary>
/// <remarks>
/// The owner's refused OB on 2026-10-30: Portal would not export the block they had just
/// edited, the host kept the previous file, and the row drew it like every other — type in
/// grey, name in the ordinary colour — so "no change" was the whole of what the screen said.
/// The builder's rule for which warning counts is tested in Core; this is the seam the panel
/// binds to.
/// </remarks>
public sealed class TreeRowExportWarningTests
{
    private static TreeRow Row(string? exportWarning, string? typeName = "Organization block") =>
        new(new ProjectTreeNode
        {
            Name = "Main",
            Path = "plc/M/Dev/PLC/Program blocks/Main.xml",
            Kind = ProjectNodeKind.Object,
            Type = "OB",
            TypeName = typeName,
            Files = ["plc/M/Dev/PLC/Program blocks/Main.xml"],
            ExportWarning = exportWarning,
        }, depth: 0);

    [Fact]
    public void A_refused_object_says_so_beside_its_type()
    {
        var row = Row("Inconsistent blocks and PLC data types (UDT) cannot be exported.");

        Assert.True(row.HasExportWarning);
        Assert.Equal("Organization block · export refused", row.Annotation);
    }

    [Fact]
    public void A_refused_object_with_no_type_label_still_says_so()
    {
        var row = Row("refused", typeName: null);

        // TypeLabel falls back to the raw type, so the word stands beside it.
        Assert.EndsWith("export refused", row.Annotation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exported_object_reports_its_type_alone()
    {
        var row = Row(null);

        Assert.False(row.HasExportWarning);
        Assert.Equal("Organization block", row.Annotation);
    }
}
