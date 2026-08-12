using Versio.Core.Git;

namespace Versio.Infrastructure.Git;

/// <summary>
/// Real-filesystem implementation of <see cref="IRepositoryProbe"/>.
/// </summary>
/// <remarks>
/// Thin by design — the interesting logic is the walk in <see cref="RepositoryLocator"/>,
/// which is unit tested against an in-memory probe. Everything here just answers questions
/// about the disk and swallows the access failures that a locked or permission-denied
/// folder produces, because "cannot read it" and "it is not there" lead to the same
/// outcome for the caller.
/// </remarks>
public sealed class FileSystemRepositoryProbe : IRepositoryProbe
{
    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string? ReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
