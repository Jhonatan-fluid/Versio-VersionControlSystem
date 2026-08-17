using Versio.Core.Plc;
using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// The vendor seam. These assert the shape a second toolchain has to satisfy, so the
/// abstraction is judged against a contract rather than against CODESYS's habits.
/// </summary>
public class PlcFormatTests
{
    private static readonly IPlcProjectFormat Codesys = CodesysProjectFormat.Instance;

    [Fact]
    public void The_CODESYS_format_identifies_itself()
    {
        Assert.Equal(PlcVendor.Codesys, Codesys.Vendor);
        Assert.Contains("CODESYS", Codesys.DisplayName);
    }

    [Theory]
    [InlineData(".project")]
    [InlineData(".library")]
    public void Project_extensions_are_declared(string extension)
    {
        Assert.Contains(extension, Codesys.ProjectExtensions);
    }

    [Fact]
    public void A_library_project_is_still_recognised_as_a_project()
    {
        // Importing one is not supported yet, but it must be SEEN so the user is told that
        // rather than left wondering why the file was ignored.
        Assert.Contains(".library", Codesys.ProjectExtensions);
    }

    [Theory]
    [InlineData("plc/MyMachine_template.project", true)]
    [InlineData("plc/MyMachine.project", false)]
    public void Templates_are_distinguishable_from_working_projects(string path, bool expected)
    {
        // Adopting a template creates a binding that exports a template into its own tree -
        // nonsense, and confusing to unpick afterwards (plan R28).
        Assert.Equal(expected, Codesys.IsTemplate(path));
    }

    [Fact]
    public void The_conventional_export_path_sits_beside_the_project()
    {
        Assert.Equal("plc/MyMachine", Codesys.ConventionalExportPath("plc/MyMachine.project"));
    }

    [Fact]
    public void Capabilities_are_declared_rather_than_assumed()
    {
        // A vendor whose only export is a proprietary blob cannot produce reviewable text,
        // and the UI has to say so instead of showing an empty diff.
        var capabilities = Codesys.Capabilities;

        Assert.True(capabilities.TextualExport);
        Assert.True(capabilities.TextualImport);
        Assert.True(capabilities.ObjectCreation);
        Assert.True(capabilities.Headless);
    }

    [Fact]
    public void The_declared_startup_cost_reflects_what_was_measured()
    {
        // 17-31 s measured on a real project. This number is why export-on-save is not the
        // default sync mode, so it must not drift back to an optimistic guess.
        var cost = Codesys.Capabilities.TypicalStartupCost;

        Assert.True(cost >= TimeSpan.FromSeconds(10),
            "a startup cost under 10 s would wrongly imply export-on-save is cheap");
    }

    [Fact]
    public void Every_declared_vendor_has_a_distinct_name()
    {
        // Guards the well-known list against a copy-paste duplicate as vendors are added.
        // It stopped being an enum on 2026-08-16 - a closed enum meant a vendor that was not
        // already listed could not be named without editing Versio.Core, which is the one
        // assembly the whole rule exists to keep untouched.
        var values = PlcVendor.Known;

        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void An_unavailable_toolchain_carries_an_explanation()
    {
        // Versio cannot install a vendor toolchain, so the message has to be enough for the
        // user to fix it themselves.
        var status = new PlcToolchainStatus
        {
            Available = false,
            Explanation = "CODESYS 3.5 was not found on this machine.",
        };

        Assert.False(status.Available);
        Assert.False(string.IsNullOrWhiteSpace(status.Explanation));
    }
}
