using Versio.Core.Locking;
using Versio.Core.Sync;
using Versio.Infrastructure.FileSystem;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Two writers of one JSON file at the same moment.
/// </summary>
/// <remarks>
/// Two bindings persisting their direction locks together both wrote <c>state.json.tmp</c>:
/// the second open failed with a sharing violation, the exception escaped the sync engine and
/// that binding's slot was never released (the 2026-10-25 review's seventh round, item 2).
/// The engine now survives the throw; this is the other half — the store does not throw. On a
/// real disk, because the collision is the filesystem's, and many times over, because a race
/// that fails one run in five is the kind this test exists to make fail every run.
/// </remarks>
public sealed class JsonFileConcurrencyTests
{
    [Fact]
    public async Task Concurrent_writes_to_one_lock_store_neither_throw_nor_corrupt_the_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "versio-jsonfile-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonLockStore(root);
            var owner = new LockOwner
            {
                Machine = "TEST-PC", User = "tester", SessionId = "s1", ProcessId = 4242,
                BootId = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero),
            };

            for (var round = 0; round < 10; round++)
            {
                await Task.WhenAll(Enumerable.Range(0, 32).Select(i => store.WriteAsync(
                    new LockState
                    {
                        RepoPath = root,
                        Owner = owner,
                        Bindings = new Dictionary<string, BindingLock>
                        {
                            [$"b{i}"] = new() { Direction = SyncDirection.Clean },
                        },
                    },
                    CancellationToken.None)));
            }

            // Whole and parseable: the last writer won, and no half-written temp was moved in.
            var written = await store.ReadAsync(CancellationToken.None);
            Assert.NotNull(written);
            Assert.Single(written!.Bindings);
            Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(root, ".versio", "local")),
                f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
