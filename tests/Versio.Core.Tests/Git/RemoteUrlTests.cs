using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Taking a remote URL apart, in every shape git actually accepts.
/// </summary>
/// <remarks>
/// Two things depend on this and would fail differently. Credential advice depends on the
/// transport — offering a sign-in button for an SSH remote is offering a button that cannot
/// work — and the merge-request link depends on the host, owner and repository, where a wrong
/// answer opens a browser on a 404.
/// </remarks>
public sealed class RemoteUrlTests
{
    [Fact]
    public void Nothing_configured_parses_to_nothing()
    {
        Assert.Equal(RemoteTransport.Unknown, RemoteUrl.Parse("").Transport);
        Assert.Equal(RemoteTransport.Unknown, RemoteUrl.Parse(null).Transport);
    }

    [Fact]
    public void An_https_url_is_taken_apart()
    {
        var url = RemoteUrl.Parse("https://github.com/jhonatanevm/Versio.git");

        Assert.Equal(RemoteTransport.Http, url.Transport);
        Assert.Equal("https", url.Protocol);
        Assert.Equal("github.com", url.Host);
        Assert.Equal("jhonatanevm", url.Owner);
        Assert.Equal("Versio", url.Repository);
        Assert.True(url.UsesCredentialHelper);
    }

    [Fact]
    public void The_git_suffix_is_optional()
    {
        Assert.Equal("Versio", RemoteUrl.Parse("https://github.com/x/Versio").Repository);
        Assert.Equal("Versio", RemoteUrl.Parse("https://github.com/x/Versio.git").Repository);
    }

    [Fact]
    public void The_scp_like_form_is_ssh()
    {
        var url = RemoteUrl.Parse("git@github.com:jhonatanevm/Versio.git");

        Assert.Equal(RemoteTransport.Ssh, url.Transport);
        Assert.Equal("github.com", url.Host);
        Assert.Equal("jhonatanevm", url.Owner);
        Assert.Equal("Versio", url.Repository);
        // No credential helper on this path at all, which is the only question anything
        // asks: SSH advice comes from what git says, not from the URL shape.
        Assert.False(url.UsesCredentialHelper);
    }

    [Fact]
    public void An_ssh_scheme_url_is_ssh_too()
    {
        var url = RemoteUrl.Parse("ssh://git@git.acme.internal:2222/team/plc.git");

        Assert.Equal(RemoteTransport.Ssh, url.Transport);

        // The port is not part of the host as a person reads it, or as a credential is keyed.
        Assert.Equal("git.acme.internal", url.Host);
        Assert.Equal("team", url.Owner);
        Assert.Equal("plc", url.Repository);
    }

    [Fact]
    public void Credentials_embedded_in_a_url_never_reach_the_host()
    {
        // https://user:token@host/... is an ordinary remote, and this value goes into log
        // lines, credential lookups and browser URLs. The secret must not travel with it.
        var url = RemoteUrl.Parse("https://someone:ghp_secret@github.com/x/y.git");

        Assert.Equal("github.com", url.Host);
        Assert.DoesNotContain("ghp_secret", url.Host);
        Assert.DoesNotContain("someone", url.Host);
    }

    [Fact]
    public void A_port_is_stripped_from_the_host()
    {
        Assert.Equal("git.acme.internal",
                     RemoteUrl.Parse("https://git.acme.internal:8443/t/p.git").Host);
    }

    [Fact]
    public void Nested_groups_stay_whole()
    {
        // GitLab allows arbitrarily deep groups, and Azure DevOps puts an organisation and a
        // project either side of a literal _git. Splitting into a single "owner" would lose
        // whichever level the forge cared about.
        var url = RemoteUrl.Parse("https://gitlab.com/acme/plant/line-3/recipes.git");

        Assert.Equal("acme/plant/line-3", url.Owner);
        Assert.Equal("recipes", url.Repository);
    }

    [Fact]
    public void An_azure_devops_url_keeps_its_shape()
    {
        var url = RemoteUrl.Parse("https://dev.azure.com/acme/Plant/_git/recipes");

        Assert.Equal("dev.azure.com", url.Host);
        Assert.Equal("acme/Plant/_git", url.Owner);
        Assert.Equal("recipes", url.Repository);
    }

    [Theory]
    [InlineData(@"C:\GIT\Versio")]
    [InlineData(@"\\fileserver\share\plc.git")]
    [InlineData("../remote.git")]
    [InlineData("/home/engineer/plc.git")]
    public void A_path_is_a_local_remote_and_authenticates_nothing(string path)
    {
        var url = RemoteUrl.Parse(path);

        // A folder is a perfectly good remote, and it is how the whole push path is tested
        // without a network.
        Assert.Equal(RemoteTransport.Local, url.Transport);
        Assert.False(url.UsesCredentialHelper);
    }

    [Fact]
    public void A_windows_path_is_not_mistaken_for_the_scp_form()
    {
        // Both carry a colon. The @ is what separates them, and getting this wrong would make
        // every local test repository look like an SSH remote.
        Assert.Equal(RemoteTransport.Local, RemoteUrl.Parse(@"C:\temp\remote.git").Transport);
    }

    [Fact]
    public void An_unrecognised_scheme_is_admitted_rather_than_guessed()
    {
        // A guess here becomes a browser opened on a 404, or sign-in advice for a remote that
        // never asks for credentials.
        Assert.Equal(RemoteTransport.Unknown, RemoteUrl.Parse("ftp://host/x/y.git").Transport);
    }

    [Theory]
    [InlineData("https://github.com/x/Versio.git", "Versio")]
    [InlineData("git@github.com:x/Versio.git", "Versio")]
    [InlineData(@"C:\GIT\Versio", "Versio")]
    [InlineData("https://github.com/x/Versio/", "Versio")]
    public void The_clone_folder_name_matches_what_git_would_choose(string url, string expected)
    {
        // This behaviour used to live in the clone dialog. Two independent parsers of a format
        // this irregular would eventually disagree about a shape one had learned and the other
        // had not.
        Assert.Equal(expected, RemoteUrl.RepositoryName(url));
    }
}
