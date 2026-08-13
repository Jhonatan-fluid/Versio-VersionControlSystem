using System.Text.RegularExpressions;
using Versio.Core.Bindings;
using Xunit;

namespace Versio.Core.Tests.Bindings;

/// <summary>
/// The two directories an export writes beside the mirror while it replaces it.
/// </summary>
/// <remarks>
/// <para>
/// The names are one convention in two languages: <c>versio_export.py</c> writes them and the
/// ignore plan covers them. A rename on one side and not the other leaves untracked
/// directories in somebody's repository that <i>Stage all</i> would cheerfully commit — which
/// is not hypothetical, because this repository's own <c>.gitignore</c> had the staging rule
/// only through somebody typing it by hand.
/// </para>
/// <para>
/// So this test reads the exporter's source. That is unusual and deliberate: the alternative
/// is an integration test that kills CODESYS mid-swap, and the thing worth pinning is that two
/// files agree about a string.
/// </para>
/// </remarks>
public sealed class SwapDirectoryTests
{
    private static string Exporter() =>
        File.ReadAllText(Path.Combine(
            TestPaths.Root, "src", "Versio.Scripts.Codesys", "versio_export.py"));

    [Fact]
    public void The_exporter_and_the_ignore_plan_use_the_same_names()
    {
        var source = Exporter();

        Assert.Contains($"\"{BindingConventions.StagingSuffix}\"", source, StringComparison.Ordinal);
        Assert.Contains($"\"{BindingConventions.TrashSuffix}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_swap_never_deletes_the_mirror_before_the_new_one_is_in_place()
    {
        // The whole point of §5.D. rmtree(out_root) followed by rename(staging, out_root)
        // left a window with NO mirror at all - not stale, not partial, none - and rmtree
        // over a 2.5 MB visualisation tree is not instantaneous.
        var source = Exporter();

        var swap = source[source.IndexOf("THREE RENAMES", StringComparison.Ordinal)..];
        var rename = swap.IndexOf("os.rename(staging, out_root)", StringComparison.Ordinal);
        var delete = swap.IndexOf("shutil.rmtree(trash)", StringComparison.Ordinal);

        Assert.True(rename > 0, "the staging rename should still be in the swap");
        Assert.True(delete > rename,
                    "the old tree must be deleted only after the new one is in place");

        // And the old tree is moved aside rather than removed.
        Assert.DoesNotContain("shutil.rmtree(out_root)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_leftover_from_either_directory_is_announced_rather_than_silently_removed()
    {
        // Both survive only when a run was killed, and each says something different about
        // where the mirror went. Clearing them quietly would delete the evidence.
        var source = Exporter();

        Assert.Contains("staging-cleared", source, StringComparison.Ordinal);
        Assert.Contains("trash-cleared", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_suffixes_are_directory_names_rather_than_extensions()
    {
        // <mirror>.versio_staging sits BESIDE the mirror, so an ignore rule for it has to be
        // a path with a suffix rather than a glob over a file extension.
        Assert.StartsWith(".", BindingConventions.StagingSuffix, StringComparison.Ordinal);
        Assert.StartsWith(".", BindingConventions.TrashSuffix, StringComparison.Ordinal);
        Assert.NotEqual(BindingConventions.StagingSuffix, BindingConventions.TrashSuffix);

        Assert.Matches(new Regex(@"^\.versio_[a-z]+$"), BindingConventions.StagingSuffix);
        Assert.Matches(new Regex(@"^\.versio_[a-z]+$"), BindingConventions.TrashSuffix);
    }
}
