using System.Text.RegularExpressions;
using Versio.Core.Bindings;
using Versio.TestSupport;
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
            TestPaths.Scripts, "versio_export.py"));

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
        var rename = swap.IndexOf("rename_retrying(staging, out_root)", StringComparison.Ordinal);
        var delete = swap.IndexOf("shutil.rmtree(trash)", StringComparison.Ordinal);

        Assert.True(rename > 0, "the staging rename should still be in the swap");
        Assert.True(delete > rename,
                    "the old tree must be deleted only after the new one is in place");

        // And the old tree is moved aside rather than removed.
        Assert.DoesNotContain("shutil.rmtree(out_root)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fallback_that_narrows_that_guarantee_says_so_where_it_is_taken()
    {
        // THE ONE PLACE §5.D DOES NOT HOLD, and the reason this test exists rather than the
        // rule simply being relaxed. A directory rename is refused on Windows while any
        // process holds a handle inside the tree — a file dialog's working directory is
        // enough — and the answer to that is to replace the mirror file by file, which means
        // a real window where the mirror is half old and half new.
        //
        // That is a genuine narrowing of the guarantee the test above pins, so it must be
        // reachable ONLY from the refusal and must be visible when it happens. An operator
        // reading "exported 18 objects" has no way to know their mirror was rebuilt the slow
        // way unless the script says so, and it will keep taking that path on every export
        // until the parked process moves.
        var source = Exporter();

        Assert.Contains("replace_contents(out_root, staging)", source, StringComparison.Ordinal);
        Assert.Contains("swap-fallback", source, StringComparison.Ordinal);

        // Guarded by the errno rather than by any refusal: a rename failing for some other
        // reason must still throw, because the fallback is only correct for a held handle.
        var swap = source[source.IndexOf("THREE RENAMES", StringComparison.Ordinal)..];
        var guard = swap.IndexOf("err.errno != errno.EACCES", StringComparison.Ordinal);
        var fallback = swap.IndexOf("replace_contents(out_root, staging)", StringComparison.Ordinal);

        Assert.True(guard > 0, "the fallback must be reached only on an access refusal");
        Assert.True(fallback > guard, "the errno check must come before the fallback");
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
