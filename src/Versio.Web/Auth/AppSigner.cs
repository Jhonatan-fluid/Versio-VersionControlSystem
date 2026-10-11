using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Versio.Web.Auth;

/// <summary>
/// Signs the GitHub App's JWT with the App's private key, wherever that key lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam for security rule 15</b> (docs/web/PLAN.md): in production the key should sign
/// inside a key vault (Azure Key Vault's "sign", AWS KMS's <c>Sign</c> with RSASSA_PKCS1_V1_5_SHA_256)
/// and never be loaded into this process. Which vault depends on where the server is hosted, which
/// is the owner's open decision, so the vault implementation is one class against this interface
/// and one registration line in WebHost. Until then <see cref="PemAppSigner"/> signs with the key
/// given as configuration.
/// </para>
/// <para>
/// The input is the JWT's <c>header.payload</c> bytes; the output is the RS256 signature (SHA-256,
/// PKCS#1 v1.5), which is exactly what both vaults' sign operations return.
/// </para>
/// </remarks>
public interface IAppSigner
{
    Task<byte[]> SignRs256Async(byte[] data, CancellationToken ct);
}

/// <summary>Signs with <see cref="GitHubOptions.PrivateKeyPem"/>, held in memory only for the call.</summary>
public sealed class PemAppSigner(IOptions<WebOptions> options) : IAppSigner
{
    public Task<byte[]> SignRs256Async(byte[] data, CancellationToken ct)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.Value.GitHub.PrivateKeyPem);
        return Task.FromResult(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
