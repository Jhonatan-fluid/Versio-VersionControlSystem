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
}
