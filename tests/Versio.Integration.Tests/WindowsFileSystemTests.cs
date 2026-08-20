using System.Text;
using Versio.Infrastructure.FileSystem;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Share-lock behaviour of the real filesystem adapter.
/// </summary>
/// <remarks>
/// The copy retry was already here for a file payload the IDE holds. Hashing a directory
/// payload includes the pointer at the top of it, and <c>File.ReadAllBytesAsync</c> opens
/// exclusive — so a finished export threw while hashing that pointer. These tests are the
/// mutation check: an exclusive <see cref="File.ReadAllBytesAsync(string)"/> would go red.
/// </remarks>
public sealed class WindowsFileSystemTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "versio-fs-" + Guid.NewGuid().ToString("N")[..8]);

    public WindowsFileSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task ReadAllBytes_succeeds_while_another_handle_has_the_file_open()
    {
        var path = Path.Combine(_dir, "pointer.bin");
        var payload = Encoding.UTF8.GetBytes("the pointer");
        File.WriteAllBytes(path, payload);

        await using var holder = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var read = await new WindowsFileSystem().ReadAllBytesAsync(path, CancellationToken.None);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadAllBytes_retries_an_exclusive_lock_until_it_releases()
    {
        var path = Path.Combine(_dir, "held.bin");
        var payload = Encoding.UTF8.GetBytes("still there");
        File.WriteAllBytes(path, payload);

        var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var pending = new WindowsFileSystem().ReadAllBytesAsync(path, CancellationToken.None);
            await Task.Delay(400);
            await holder.DisposeAsync();
            holder = null!;

            Assert.Equal(payload, await pending);
        }
        finally
        {
            if (holder is not null)
            {
                await holder.DisposeAsync();
            }
        }
    }
}
