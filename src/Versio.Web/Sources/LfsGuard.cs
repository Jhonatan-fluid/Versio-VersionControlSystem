using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Versio.Web.Sources;

/// <summary>A Git LFS pointer: the file a blob stands in for.</summary>
public sealed record LfsPointer(string Oid, long Size)
{
    private const string Version = "version https://git-lfs.github.com/spec/v1";

    /// <summary>The pointer in <paramref name="blob"/>, or null when the blob is a real file.</summary>
    /// <remarks>
    /// A pointer is a few lines of ASCII under a kilobyte; anything bigger is content, so a large
    /// file is never decoded just to find out.
    /// </remarks>
    public static LfsPointer? Parse(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length is 0 or > 1024)
        {
            return null;
        }

        var text = System.Text.Encoding.ASCII.GetString(blob);
        if (!text.StartsWith(Version, StringComparison.Ordinal))
        {
            return null;
        }

        string? oid = null;
        long size = -1;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
            {
                oid = line["oid sha256:".Length..].Trim();
            }
            else if (line.StartsWith("size ", StringComparison.Ordinal)
                     && long.TryParse(line["size ".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            {
                size = s;
            }
        }

        return oid is { Length: 64 } && oid.All(Uri.IsHexDigit) && size >= 0 ? new LfsPointer(oid, size) : null;
    }
}

/// <summary>
/// Where an LFS download may go: https, an allowlisted host, a public address, no redirects.
/// </summary>
/// <remarks>
/// <para>
/// <b>The LFS server chooses the download address, and whoever controls a repository's LFS
/// configuration controls the LFS server's answer</b> (docs/web/PLAN.md, security rule 4).
/// Followed blindly, that address can be <c>169.254.169.254</c>, the cloud's metadata service,
/// which hands out the server's own credentials. Three walls:
/// </para>
/// <list type="number">
/// <item>the address must be https and its host on <see cref="GitHubOptions.LfsHosts"/>;</item>
/// <item>the connection is made by <see cref="CreateHandler"/>, whose connect callback resolves
/// the host itself and refuses any loopback, private, link-local, unique-local or multicast
/// address <i>at the moment of connecting</i>, so a DNS answer that changes between a check and a
/// connect (rebinding) cannot slip an internal address through;</item>
/// <item>redirects are not followed, so an allowlisted host cannot bounce the request elsewhere.</item>
/// </list>
/// <para>The container's outbound allowlist (security rule 16) is the fourth, outside this code.</para>
/// </remarks>
public static class LfsGuard
{
    /// <summary>Whether <paramref name="href"/> is an address a download may even be attempted at.</summary>
    public static bool IsAllowed(string? href, IReadOnlyCollection<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        return Uri.TryCreate(href, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether an address belongs to the machine, its network, or nobody in particular.</summary>
    public static bool IsInternal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                   || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10                                   // 10/8
               || b[0] == 0                                 // 0/8
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16/12
               || (b[0] == 192 && b[1] == 168)              // 192.168/16
               || (b[0] == 169 && b[1] == 254)              // link-local, the metadata service
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
               || b[0] >= 224;                              // multicast and reserved
    }

    /// <summary>
    /// The handler for LFS downloads: no redirects, no proxy, and a connect callback that refuses
    /// internal addresses after resolving the host itself.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var target = addresses.FirstOrDefault(a => !IsInternal(a))
                         ?? throw new HttpRequestException($"LFS download host {context.DnsEndPoint.Host} resolves only to internal addresses; refused.");
            if (addresses.Any(IsInternal))
            {
                // A name that answers with both kinds is either misconfigured or an attack; neither is a host to trust.
                throw new HttpRequestException($"LFS download host {context.DnsEndPoint.Host} resolves to an internal address; refused.");
            }

            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
