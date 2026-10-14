using System.Net;
using System.Text;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Security rule 4: an LFS download goes only to an allowlisted https host on a public address.
/// </summary>
public sealed class LfsGuardTests
{
    private static readonly string[] Hosts = ["github-cloud.githubusercontent.com"];

    [Theory]
    [InlineData("https://github-cloud.githubusercontent.com/alambic/media/1", true)]
    [InlineData("http://github-cloud.githubusercontent.com/alambic/media/1", false)]
    [InlineData("https://github-cloud.githubusercontent.com:8443/x", false)]
    [InlineData("https://user:pw@github-cloud.githubusercontent.com/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data/", false)]
    [InlineData("https://github-cloud.githubusercontent.com.evil.example/x", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public void Only_https_on_an_allowlisted_host_is_attempted(string href, bool allowed)
    {
        Assert.Equal(allowed, LfsGuard.IsAllowed(href, Hosts));
    }

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("140.82.112.3", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:50c0:8000::154", false)]
    public void Internal_addresses_are_recognised_as_internal(string address, bool isInternal)
    {
        Assert.Equal(isInternal, LfsGuard.IsInternal(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task The_handler_refuses_to_connect_to_an_internal_address_even_through_an_allowed_name()
    {
        // localhost resolves to loopback: whatever name was allowed, the connect callback looks
        // at the address it is about to dial.
        using var client = new HttpClient(LfsGuard.CreateHandler());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://localhost/x"));
    }

    [Fact]
    public void A_pointer_is_read_and_real_content_is_not_mistaken_for_one()
    {
        var oid = new string('a', 64);
        var pointer = Encoding.ASCII.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize 12345\n");

        Assert.Equal(new LfsPointer(oid, 12345), LfsPointer.Parse(pointer));
        Assert.Null(LfsPointer.Parse(Encoding.ASCII.GetBytes("TYPE ST_X : STRUCT END_STRUCT END_TYPE")));
        Assert.Null(LfsPointer.Parse(Encoding.ASCII.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid[..10]}\nsize 1\n")));
    }
}
