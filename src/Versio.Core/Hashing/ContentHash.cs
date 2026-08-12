using System.Security.Cryptography;
using System.Text;

namespace Versio.Core.Hashing;

/// <summary>
/// Stable content hashing, used by the hash gate and the manifest.
/// </summary>
/// <remarks>
/// <para>
/// Vendor-neutral on purpose. This hashes binary project files, JSON manifests and exported
/// text alike; it previously lived on the Structured Text codec, which meant the sync
/// orchestrator reached into an IEC 61131-3 type to hash a CODESYS binary. The vendor
/// reorganisation surfaced that as the mislocation it was.
/// </para>
/// <para>
/// <b>UTF-8 without a BOM, always.</b> A BOM changes the hash of otherwise identical text,
/// which would make a file written by one tool look different from the same file written by
/// another — and the hash gate would then re-export forever.
/// </para>
/// </remarks>
public static class ContentHash
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Hash of text, encoded as UTF-8 without a BOM.</summary>
    public static string Of(string text) => Of(Utf8NoBom.GetBytes(text));

    /// <summary>Hash of raw bytes — used for the binary <c>.project</c>.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// One hash for a whole directory, from each file's path and content hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The path is hashed as well as the content.</b> Renaming <c>A.st</c> to <c>B.st</c>
    /// changes nothing about the bytes in the tree, and a content-only hash would call the
    /// tree unchanged — so the rename would never be imported.
    /// </para>
    /// <para>
    /// Sorted here rather than trusted from the caller. Directory enumeration order is an
    /// operating-system detail, and a hash that depended on it would differ between machines
    /// and re-sync forever with nothing having changed.
    /// </para>
    /// </remarks>
    /// <param name="files">Relative path and content hash of every file in the tree.</param>
    public static string OfTree(IEnumerable<(string Path, string Hash)> files)
    {
        var builder = new StringBuilder();

        foreach (var (path, hash) in files
                     .Select(f => (Path: Normalize(f.Path), f.Hash))
                     .OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            // A NUL separator, because it is the one byte that cannot appear in a path or in
            // a hex hash — so no two different trees can flatten to the same string.
            builder.Append(path).Append('\0').Append(hash).Append('\n');
        }

        return Of(builder.ToString());
    }

    /// <summary>Forward slashes, so a tree hashes the same on any platform.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/');
}
