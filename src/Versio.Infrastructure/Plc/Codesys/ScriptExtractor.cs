using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Versio.Infrastructure.Plc.Codesys;

/// <summary>
/// Writes the embedded python scripts to a temp directory so CODESYS can run them.
/// </summary>
/// <remarks>
/// The scripts ship inside the assembly and are re-extracted when their content hash
/// changes, so they always match the app version. A stale copy left on disk from a previous
/// install would produce failures that look like CODESYS bugs.
/// </remarks>
public sealed class ScriptExtractor
{
    private readonly Assembly _assembly = typeof(ScriptExtractor).Assembly;
    private readonly Lazy<string> _root;

    public ScriptExtractor(string? rootOverride = null)
    {
        _root = new Lazy<string>(() => rootOverride ?? Extract());
    }

    public string Root => _root.Value;

    public string PathFor(string scriptName)
    {
        var path = Path.Combine(Root, scriptName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"script '{scriptName}' was not embedded in {_assembly.GetName().Name}", path);
        }

        return path;
    }

    public IReadOnlyList<string> ScriptNames() =>
        _assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            .Select(ShortName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private string Extract()
    {
        var resources = _assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (resources.Count == 0)
        {
            throw new InvalidOperationException(
                $"no python scripts embedded in {_assembly.GetName().Name}");
        }

        // Version the directory by content, so an upgrade cannot pick up old scripts and a
        // downgrade cannot pick up new ones.
        var stamp = ContentStamp(resources);
        var dir = Path.Combine(Path.GetTempPath(), "Versio", "scripts", stamp);
        Directory.CreateDirectory(dir);

        foreach (var resource in resources)
        {
            var target = Path.Combine(dir, ShortName(resource));
            if (File.Exists(target))
            {
                continue;
            }

            using var stream = _assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"cannot open resource {resource}");
            using var file = File.Create(target);
            stream.CopyTo(file);
        }

        return dir;
    }

    private string ContentStamp(IEnumerable<string> resources)
    {
        var sb = new StringBuilder();
        foreach (var resource in resources.OrderBy(r => r, StringComparer.Ordinal))
        {
            using var stream = _assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            sb.Append(resource).Append(':')
              .Append(Convert.ToHexString(SHA256.HashData(stream))[..16]).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]
            .ToLowerInvariant();
    }

    private static string ShortName(string resourceName)
    {
        var i = resourceName.LastIndexOf('.', resourceName.Length - ".py".Length - 1);
        return i < 0 ? resourceName : resourceName[(i + 1)..];
    }
}
