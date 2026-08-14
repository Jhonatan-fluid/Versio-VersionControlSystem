using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests;

/// <summary>
/// The status light reports the worst thing that is true, not the last thing checked.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> The status bar drew a hardcoded <c>Brush.Status.Added</c> ellipse — green,
/// leftmost, shaped exactly like a health indicator, and green while git was missing, while the
/// tree was mid-merge, while a sync had failed and while the branch had diverged. Every one of
/// those states was computed by the shell and none of them reached the dot.
/// </para>
/// <para>
/// What is worth pinning is the <i>precedence</i>. Several conditions are routinely true at
/// once — a mid-merge branch is usually diverged too, a machine with no git also has no
/// identity — so the interesting question is never "does it turn amber" but "which of the
/// three amber reasons does it name". None of that needs a repository, a process or a clock.
/// </para>
/// </remarks>
public sealed class ShellHealthTests
{
    /// <summary>Everything wrong at once, so each test can turn conditions off rather than on.</summary>
    private static readonly ShellConditions Everything = new()
    {
        RepositoryOpen = true,
        GitMissing = true,
        FailedProject = "Conveyor.project",
        MidMerge = true,
        IdentityMissing = true,
        AuthNeedsAttention = true,
        Diverged = true,
    };

    private static readonly ShellConditions Clean = new()
    {
        RepositoryOpen = true,
    };

    [Fact]
    public void Nothing_wrong_is_green()
    {
        var health = ShellHealth.Evaluate(Clean);

        Assert.Equal("Brush.Status.Added", health.BrushKey);
    }

    [Fact]
    public void No_repository_is_neutral_rather_than_green()
    {
        // The state the app launches in. A green light on an empty shell would be reporting
        // the health of nothing, which is the same mistake as the hardcoded dot in miniature.
        var health = ShellHealth.Evaluate(new ShellConditions { RepositoryOpen = false });

        Assert.Equal("Brush.Status.Neutral", health.BrushKey);
        Assert.Equal("No repository open", health.Summary);
    }

    [Fact]
    public void Missing_git_outranks_everything_including_having_no_repository()
    {
        // First, because nothing else in the application can work without it - the same
        // argument the git panel's own banner order makes.
        var health = ShellHealth.Evaluate(Everything with { RepositoryOpen = false });

        Assert.Equal("Brush.Status.Deleted", health.BrushKey);
        Assert.Contains("not available", health.Summary);
    }

    [Fact]
    public void A_failed_sync_outranks_a_merge_and_names_the_project()
    {
        // A failed project stops syncing until a person has seen the failure, and that gate is
        // deliberate - so it is what the light should be pointing at.
        var health = ShellHealth.Evaluate(Everything with { GitMissing = false });

        Assert.Equal("Brush.Status.Deleted", health.BrushKey);
        Assert.Contains("Conveyor.project", health.Summary);
    }

    [Fact]
    public void A_merge_in_progress_is_the_conflict_colour()
    {
        var health = ShellHealth.Evaluate(
            Everything with { GitMissing = false, FailedProject = "" });

        Assert.Equal("Brush.Status.Conflict", health.BrushKey);
    }

    [Theory]
    [InlineData("Git cannot name a committer yet")]
    public void A_missing_identity_is_amber_and_leads_the_other_two(string expected)
    {
        // Identity, auth and divergence are all amber, so the colour cannot tell them apart -
        // which makes the sentence the whole of the information.
        var health = ShellHealth.Evaluate(Everything with
        {
            GitMissing = false,
            FailedProject = "",
            MidMerge = false,
        });

        Assert.Equal("Brush.Status.Modified", health.BrushKey);
        Assert.Equal(expected, health.Summary);
    }

    [Fact]
    public void Auth_outranks_divergence()
    {
        var health = ShellHealth.Evaluate(new ShellConditions
        {
            RepositoryOpen = true,
            AuthNeedsAttention = true,
            Diverged = true,
        });

        Assert.Equal("Brush.Status.Modified", health.BrushKey);
        Assert.Contains("remote needs attention", health.Summary);
    }

    [Fact]
    public void Divergence_alone_is_amber()
    {
        var health = ShellHealth.Evaluate(Clean with { Diverged = true });

        Assert.Equal("Brush.Status.Modified", health.BrushKey);
        Assert.Contains("both moved", health.Summary);
    }

    [Fact]
    public void Every_outcome_carries_a_sentence()
    {
        // A coloured dot with no explanation is a riddle. Checked across every branch rather
        // than per case, so a condition added later without a summary fails here.
        ShellConditions[] cases =
        [
            Clean,
            new() { RepositoryOpen = false },
            Everything,
            Everything with { GitMissing = false },
            Everything with { GitMissing = false, FailedProject = "" },
            Clean with { IdentityMissing = true },
            Clean with { AuthNeedsAttention = true },
            Clean with { Diverged = true },
        ];

        foreach (var conditions in cases)
        {
            var health = ShellHealth.Evaluate(conditions);

            Assert.False(string.IsNullOrWhiteSpace(health.Summary));
            Assert.StartsWith("Brush.Status.", health.BrushKey);
        }
    }
}
