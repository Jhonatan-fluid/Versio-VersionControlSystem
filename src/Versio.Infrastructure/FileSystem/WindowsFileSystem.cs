using System.Text;
using Versio.Core.Abstractions;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// <see cref="IFileSystem"/> over <c>System.IO</c>.
/// </summary>
/// <remarks>
/// Thin by design — this is verified by integration tests rather than unit tests, so every
/// line here is a line that cannot be cheaply proven. The non-obvious behaviour is surviving
/// a share lock: CODESYS holds the <c>.project</c> open while the engineer works, and a
/// directory payload includes a pointer file the toolchain still holds after its host has
/// exited. Copy and hash both have to wait that out rather than failing the whole sync.
/// </remarks>
public sealed class WindowsFileSystem(int copyRetries = 5) : IFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <summary>
    /// Read a file that another process may still be holding.
    /// </summary>
    /// <remarks>
    /// <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/> opens exclusive.
    /// Hashing a directory payload includes the pointer sitting at the top of it, and the
    /// toolchain that just exported still holds that pointer — measured 2026-08-19: host
    /// <c>WaitForExit</c> had returned, export had written the mirror, then
    /// <c>ProjectHashAsync</c> threw and the session was logged as "failed to open at
    /// startup". The watcher already reads with <see cref="FileShare.ReadWrite"/>; hashing
    /// must too, and retry the way <see cref="CopyAsync"/> already does, because an
    /// exclusive holder refuses even a sharing reader until it lets go.
    /// </remarks>
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = stream.Length;
                if (length > int.MaxValue)
                {
                    throw new IOException($"'{path}' is too large to read as one buffer.");
                }

                var bytes = new byte[length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var n = await stream.ReadAsync(bytes.AsMemory(offset), ct)
                        .ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    offset += n;
                }

                if (offset == bytes.Length)
                {
                    return bytes;
                }

                var slice = new byte[offset];
                Buffer.BlockCopy(bytes, 0, slice, 0, offset);
                return slice;
            }
            catch (IOException) when (attempt < copyRetries)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }
    }

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct) =>
        File.ReadAllTextAsync(path, Utf8NoBom, ct);

    public async Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // UTF-8 without BOM, always. A BOM changes the content hash of otherwise identical
        // text, and IronPython 2.7 cannot read a BOM'd JSON file at all.
        await File.WriteAllTextAsync(path, content, Utf8NoBom, ct).ConfigureAwait(false);
    }

    public IEnumerable<string> EnumerateFiles(string root, string searchPattern = "*") =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories)
            : [];

    public async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < copyRetries)
            {
                // Almost always CODESYS mid-save. Back off and try again rather than
                // failing a sync over a lock that lasts a few hundred milliseconds.
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }
    }

    public async Task CopyTreeAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            await CopyAsync(file, Path.Combine(destination, relative), ct).ConfigureAwait(false);
        }
    }

    public IEnumerable<string> EnumerateDirectories(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
            : [];

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
}
