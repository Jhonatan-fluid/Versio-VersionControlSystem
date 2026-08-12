using Versio.Core.Hashing;
using Xunit;

namespace Versio.Core.Tests.Hashing;

/// <summary>
/// The tree hash the direction lock depends on.
/// </summary>
/// <remarks>
/// Every property below is one whose absence produces a specific, silent failure: a missed
/// import, an endless re-sync, or two different trees that compare equal.
/// </remarks>
public sealed class ContentHashTreeTests
{
    private static readonly (string Path, string Hash)[] Tree =
    [
        ("Simulation.st", "sha256:aaa"),
        ("sub/PLC_PRG.xml", "sha256:bbb"),
        ("_manifest.json", "sha256:ccc"),
    ];

    [Fact]
    public void Enumeration_order_does_not_change_the_hash()
    {
        // Directory order is an operating-system detail. A hash that depended on it would
        // differ between machines and re-sync forever with nothing having changed.
        var forward = ContentHash.OfTree(Tree);
        var reversed = ContentHash.OfTree(Tree.Reverse());

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Changing_one_file_changes_the_hash()
    {
        var edited = Tree.Select(f =>
            f.Path == "Simulation.st" ? (f.Path, "sha256:zzz") : f);

        Assert.NotEqual(ContentHash.OfTree(Tree), ContentHash.OfTree(edited));
    }

    [Fact]
    public void Renaming_a_file_changes_the_hash_although_no_bytes_moved()
    {
        // The reason paths are hashed at all. A content-only hash calls this tree unchanged,
        // and the rename is never imported.
        var renamed = Tree.Select(f =>
            f.Path == "Simulation.st" ? ("Simulation2.st", f.Hash) : f);

        Assert.NotEqual(ContentHash.OfTree(Tree), ContentHash.OfTree(renamed));
    }

    [Fact]
    public void Adding_and_removing_a_file_both_change_the_hash()
    {
        var added = Tree.Append(("New.st", "sha256:ddd"));
        var removed = Tree.Where(f => f.Path != "Simulation.st");

        Assert.NotEqual(ContentHash.OfTree(Tree), ContentHash.OfTree(added));
        Assert.NotEqual(ContentHash.OfTree(Tree), ContentHash.OfTree(removed));
    }

    [Fact]
    public void Separators_do_not_change_the_hash()
    {
        // The same tree read through a Windows path and a repo-relative path must agree, or
        // the recorded baseline never matches what is measured next time.
        var windows = new[] { (@"sub\PLC_PRG.xml", "sha256:bbb") };
        var posix = new[] { ("sub/PLC_PRG.xml", "sha256:bbb") };

        Assert.Equal(ContentHash.OfTree(windows), ContentHash.OfTree(posix));
    }

    [Fact]
    public void Fields_cannot_be_confused_across_the_separator()
    {
        // Concatenating path and hash without a separator that cannot occur in either lets
        // two different trees flatten to one string — and then compare equal.
        var a = new[] { ("a", "sha256:bc"), ("d", "sha256:e") };
        var b = new[] { ("a", "sha256:b"), ("cd", "sha256:e") };

        Assert.NotEqual(ContentHash.OfTree(a), ContentHash.OfTree(b));
    }

    [Fact]
    public void An_empty_tree_hashes_to_something_stable()
    {
        Assert.Equal(ContentHash.OfTree([]), ContentHash.OfTree([]));
        Assert.StartsWith("sha256:", ContentHash.OfTree([]));
    }
}
