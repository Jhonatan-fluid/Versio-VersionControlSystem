using System.Text;
using Versio.Core.Abstractions;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// <see cref="IFileSystem"/> over <c>System.IO</c>.
/// </summary>
/// <remarks>
/// Thin by design — this is verified by integration tests rather than unit tests, so every
/// line here is a line that cannot be cheaply proven. The only non-obvious behaviour is the
/// copy retry, and it exists for a specific reason: CODESYS holds the <c>.project</c> open
/// while the engineer works, so the export pipeline copies it rather than reading in place,
/// and that copy has to survive a transient share lock instead of failing the whole sync.
/// </remarks>
public sealed class WindowsFileSystem(int copyRetries = 5) : IFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct) =>
        File.ReadAllBytesAsync(path, ct);

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
