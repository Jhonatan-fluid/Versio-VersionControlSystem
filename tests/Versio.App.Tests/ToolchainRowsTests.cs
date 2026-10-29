using Versio.App.ViewModels;
using Versio.Core.Plc;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The System information rows for one toolchain, against made-up statuses.
/// </summary>
/// <remarks>
/// <b>Review, 2026-10-29.</b> A vendor that can choose an install per project (TIA Portal,
/// from the extension) reports every installed release in <c>Candidates</c> while available.
/// The rows rendered that list as "Also found" under a comment calling it a problem, and the
/// vendor's sentence on how the choice is made never reached the window, because the
/// explanation was shown for a refusal alone. The method probes the real machine, so the row
/// builder was split out to be checked here without one.
/// </remarks>
public sealed class ToolchainRowsTests
{
    private static readonly IPlcProjectFormat Format = Versio.Plc.TiaPortal.TiaPortalProjectFormat.Instance;

    [Fact]
    public void Two_installed_releases_are_Installed_rows_with_how_the_choice_is_made()
    {
        var rows = SyncViewModel.DescribeToolchain(Format, new PlcToolchainStatus
        {
            Available = true,
            Version = "V21, V20",
            Profile = "V21",
            ExecutablePath = @"C:\nowhere\tia\Versio.Plc.TiaPortal.Host.exe",
            Candidates = ["V21", "V20"],
            Explanation = @"C:\P\Portal V21 — V21; C:\P\Portal V20 — V20. The project file's extension picks which one opens it.",
        });

        Assert.Equal(
            ["Status", "Version", "Profile", "Executable", "Installed", "Installed", "Choice", "Projects"],
            rows.Select(r => r.Label));
        Assert.Equal("available", rows[0].Value);
        Assert.Equal(["V21", "V20"], rows.Where(r => r.Label == "Installed").Select(r => r.Value));
        Assert.Contains("extension picks which one opens it", rows.Single(r => r.Label == "Choice").Value);
        Assert.DoesNotContain(rows, r => r.Label == "Also found");
    }

    [Fact]
    public void A_refused_choice_is_still_Also_found_under_the_refusal()
    {
        // The first vendor's shape: several profiles, nothing on the file to choose by.
        var rows = SyncViewModel.DescribeToolchain(Format, new PlcToolchainStatus
        {
            Available = false,
            Candidates = ["CODESYS V3.5 SP22 Patch 2", "CODESYS V3.5 SP21"],
            Explanation = "2 profiles installed — pick one.",
        });

        Assert.Equal("2 profiles installed — pick one.", rows[0].Value);
        Assert.Equal(2, rows.Count(r => r.Label == "Also found"));
        Assert.DoesNotContain(rows, r => r.Label is "Installed" or "Choice");
    }

    [Fact]
    public async Task A_remedy_is_a_Fix_row_whose_button_runs_the_fix_and_returns_its_sentence()
    {
        var ran = 0;
        var status = new PlcToolchainStatus
        {
            Available = false,
            Version = "V21",
            Explanation = "TIA Portal V21 is installed, but X is not in the group.",
            Remedy = new PlcToolchainRemedy
            {
                Action = "Add this account to the Siemens TIA Openness group…",
                Explanation = "Adds X to the local Windows group 'Siemens TIA Openness'.",
                Afterwards = "Sign out of Windows and back in, then try again.",
            },
        };

        var rows = SyncViewModel.DescribeToolchain(Format, status, () =>
        {
            ran++;
            return Task.FromResult("Added X to 'Siemens TIA Openness'.");
        });

        var fix = rows.Single(r => r.Label == "Fix");
        Assert.Equal(
            "Adds X to the local Windows group 'Siemens TIA Openness'. Sign out of Windows and back in, then try again.",
            fix.Value);
        Assert.True(fix.HasAction);
        Assert.Equal("Add this account to the Siemens TIA Openness group…", fix.Action!.Label);
        Assert.Equal("Added X to 'Siemens TIA Openness'.", await fix.Action.Run());
        Assert.Equal(1, ran);

        // A caller that cannot act gets the words and no button.
        var readOnly = SyncViewModel.DescribeToolchain(Format, status);
        Assert.False(readOnly.Single(r => r.Label == "Fix").HasAction);
    }

    [Fact]
    public void One_install_has_no_list_at_all()
    {
        var rows = SyncViewModel.DescribeToolchain(Format, new PlcToolchainStatus
        {
            Available = true,
            Version = "V20",
            Profile = "V20",
            ExecutablePath = @"C:\nowhere\tia\Versio.Plc.TiaPortal.Host.exe",
            Explanation = @"C:\P\Portal V20 — V20",
        });

        Assert.Equal(["Status", "Version", "Profile", "Executable", "Projects"], rows.Select(r => r.Label));
    }
}
