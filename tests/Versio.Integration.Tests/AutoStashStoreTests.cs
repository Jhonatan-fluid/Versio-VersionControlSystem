using Versio.Core.Guarding;
using Versio.Core.Sync;
using Versio.Infrastructure.FileSystem;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The auto-stash sidecar against a real filesystem.
/// </summary>
/// <remarks>
/// <para>
/// The rules are unit-tested without a disk; what this covers is the half that only a real
/// filesystem can answer — that what is written comes back, that a corrupt file costs the offer
/// to restore and nothing more, and that the record lands somewhere <c>.gitignore</c> already
/// covers. That last one is not pedantry: this file names branches and commit ids from one
/// machine, and a version of it committed and pulled onto a colleague's machine would offer
/// them stashes that do not exist in their clone.
/// </para>
/// <para>
/// No CODESYS and no git process, so it never skips.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutoStashStoreTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "versio-autostash-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private JsonAutoStashStore Store() => new(_root, new TestLogger(output));

    private static AutoStashRecord Record(string id, string branch = "main") => new()
    {
        Branch = branch,
        StashCommitId = id,
        Message = AutoStash.Marker(branch, "abc1234"),
        CreatedUtc = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
        HeadCommit = "9852b2901ac6b7e5f4d3c2b1a09876543210fedc",
        FileCount = 3,
        Locks =
        [
            new StashedLock
            {
                BindingId = "b-1",
                Direction = SyncDirection.TextAuthoritative,
                ProjectHash = "sha256:aaa",
                MirrorHash = "sha256:bbb",
            },
        ],
    };

    [Fact]
    public async Task Nothing_written_reads_back_as_nothing_stashed()
    {
        // Not an error: the ordinary state of a repository nobody has switched away from
        // with work in progress.
        var state = await Store().ReadAsync(CancellationToken.None);

        Assert.Empty(state.Entries);
    }

    [Fact]
    public async Task A_record_survives_the_round_trip_with_its_locks()
    {
        var store = Store();
        await store.WriteAsync(
            AutoStash.With(AutoStashState.Empty, Record("2a4b8cdc")), CancellationToken.None);

        var back = Assert.Single((await store.ReadAsync(CancellationToken.None)).Entries);

        Assert.Equal("2a4b8cdc", back.StashCommitId);
        Assert.Equal("main", back.Branch);
        Assert.Equal(3, back.FileCount);
        Assert.True(back.IncludedUntracked);

        // The half that makes it a round trip rather than a file copy. A direction written as
        // a number would come back as one too, and a schema change would silently renumber
        // every stashed lock - hence the string converter.
        var held = Assert.Single(back.Locks);
        Assert.Equal(SyncDirection.TextAuthoritative, held.Direction);
        Assert.Equal("sha256:bbb", held.MirrorHash);
    }

    [Fact]
    public async Task The_direction_is_stored_by_name_rather_than_by_number()
    {
        var store = Store();
        await store.WriteAsync(
            AutoStash.With(AutoStashState.Empty, Record("2a4b8cdc")), CancellationToken.None);

        var json = await File.ReadAllTextAsync(store.Path);
        output.WriteLine(json);

        Assert.Contains("TextAuthoritative", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_lands_under_the_directory_gitignore_already_covers()
    {
        // .versio/local/ is what adoption writes into .gitignore. A record of one machine's
        // stashes committed and pulled onto another would offer somebody stashes that do not
        // exist in their clone.
        var store = Store();
        await store.WriteAsync(
            AutoStash.With(AutoStashState.Empty, Record("2a4b8cdc")), CancellationToken.None);

        var relative = Path.GetRelativePath(_root, store.Path).Replace('\\', '/');

        Assert.StartsWith(
            Versio.Core.Bindings.BindingConventions.LocalIgnorePattern,
            relative,
            StringComparison.Ordinal);

        Assert.True(File.Exists(store.Path));
    }

    [Fact]
    public async Task A_corrupt_record_costs_the_offer_and_nothing_else()
    {
        // Every stash is still in git; what is lost is knowing which of them were ours. The
        // app must open, so this returns empty rather than throwing - and says so loudly in
        // the log, because a silently forgotten stash is a stash nobody goes looking for.
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        await File.WriteAllTextAsync(store.Path, "{ this was half written when the power went");

        var state = await store.ReadAsync(CancellationToken.None);

        Assert.Empty(state.Entries);
    }

    [Fact]
    public async Task Writing_twice_replaces_rather_than_appends()
    {
        var store = Store();

        await store.WriteAsync(
            AutoStash.With(AutoStashState.Empty, Record("first")), CancellationToken.None);
        await store.WriteAsync(
            AutoStash.With(AutoStashState.Empty, Record("second")), CancellationToken.None);

        var entries = (await store.ReadAsync(CancellationToken.None)).Entries;

        Assert.Equal("second", Assert.Single(entries).StashCommitId);

        // The temp file used for the atomic replace does not survive as litter beside it.
        Assert.False(File.Exists(store.Path + ".tmp"));
    }
}
