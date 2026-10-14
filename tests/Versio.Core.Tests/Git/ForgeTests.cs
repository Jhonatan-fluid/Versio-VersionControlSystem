using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Which forge a remote belongs to, and where its request pages live.
/// </summary>
/// <remarks>
/// <b>The negative case is the one that matters.</b> Every other assertion here is a template
/// somebody can look up; "an unidentifiable host yields no URL" is the rule that stops this
/// feature opening a browser on a 404 with Versio's name on it. Self-hosted instances are the
/// normal case in this industry — a GitLab at <c>git.acme.internal</c> looks like nothing in
/// particular — so the fallback is exercised more than any single template.
/// </remarks>
public sealed class ForgeTests
{
    private static RemoteUrl Url(string raw) => RemoteUrl.Parse(raw);

    // ---- detection ----------------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/acme/machine.git", ForgeKind.GitHub)]
    [InlineData("git@github.com:acme/machine.git", ForgeKind.GitHub)]
    [InlineData("https://gitlab.com/acme/group/machine.git", ForgeKind.GitLab)]
    [InlineData("git@gitlab.com:acme/machine.git", ForgeKind.GitLab)]
    [InlineData("https://dev.azure.com/acme/Plant/_git/machine", ForgeKind.AzureDevOps)]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Plant/machine", ForgeKind.AzureDevOps)]
    [InlineData("https://bitbucket.org/acme/machine.git", ForgeKind.Bitbucket)]
    public void A_known_host_identifies_its_forge(string raw, ForgeKind expected) =>
        Assert.Equal(expected, Forge.Detect(Url(raw)));

    [Theory]
    [InlineData("https://git.acme.internal/group/machine.git")]
    [InlineData("git@git.acme.internal:group/machine.git")]
    [InlineData("https://source.example.co.uk/acme/machine.git")]
    public void A_self_hosted_host_identifies_nothing(string raw) =>
        Assert.Equal(ForgeKind.Unknown, Forge.Detect(Url(raw)));

    [Fact]
    public void A_local_remote_is_never_a_forge()
    {
        Assert.Equal(ForgeKind.Unknown, Forge.Detect(Url(@"C:\repos\machine.git")));
        Assert.Equal(ForgeKind.Unknown, Forge.Detect(Url(@"\\fileserver\repos\machine.git")));
        Assert.Equal(ForgeKind.Unknown, Forge.Detect(RemoteUrl.None));
    }

    [Fact]
    public void The_configured_forge_wins_over_the_hostname()
    {
        // The case this setting exists for: a self-hosted GitLab nothing can recognise.
        Assert.Equal(
            ForgeKind.GitLab,
            Forge.Detect(Url("https://git.acme.internal/group/machine.git"), ForgeKind.GitLab));

        // And the case it would be useless without: an enterprise GitHub behind its own name,
        // where the hostname is wrong rather than merely unhelpful.
        Assert.Equal(
            ForgeKind.GitHub,
            Forge.Detect(Url("https://code.acme.com/acme/machine.git"), ForgeKind.GitHub));
    }

    // ---- create URLs, every forge, both shapes ------------------------------------------

    [Theory]
    [InlineData(ForgeKind.GitHub,
        "https://github.com/acme/machine.git",
        "https://github.com/acme/machine/compare/main...feature/pumps?expand=1")]
    [InlineData(ForgeKind.GitHub,
        "git@github.com:acme/machine.git",
        "https://github.com/acme/machine/compare/main...feature/pumps?expand=1")]
    [InlineData(ForgeKind.GitLab,
        "https://gitlab.com/acme/sub/machine.git",
        "https://gitlab.com/acme/sub/machine/-/merge_requests/new" +
        "?merge_request[source_branch]=feature%2Fpumps&merge_request[target_branch]=main")]
    [InlineData(ForgeKind.AzureDevOps,
        "https://dev.azure.com/acme/Plant/_git/machine",
        "https://dev.azure.com/acme/Plant/_git/machine/pullrequestcreate" +
        "?sourceRef=feature%2Fpumps&targetRef=main")]
    // The SSH endpoint is not the web host and v3/… is not the web path; the link is
    // translated to the only shape Azure's web UI serves. Re-scheming the authority —
    // what every other forge needs — produced https://ssh.dev.azure.com/v3/… here: a
    // live button whose every click was a 404.
    [InlineData(ForgeKind.AzureDevOps,
        "git@ssh.dev.azure.com:v3/acme/Plant/machine",
        "https://dev.azure.com/acme/Plant/_git/machine/pullrequestcreate" +
        "?sourceRef=feature%2Fpumps&targetRef=main")]
    [InlineData(ForgeKind.Bitbucket,
        "https://bitbucket.org/acme/machine.git",
        "https://bitbucket.org/acme/machine/pull-requests/new?source=feature%2Fpumps&dest=main")]
    [InlineData(ForgeKind.Gitea,
        "https://git.acme.internal/acme/machine.git",
        "https://git.acme.internal/acme/machine/compare/main...feature/pumps")]
    public void The_create_page_is_pre_filled_with_both_branches(
        ForgeKind kind, string remote, string expected) =>
        Assert.Equal(expected, Forge.CreateUrl(kind, Url(remote), "feature/pumps", "main"));

    [Fact]
    public void An_azure_ssh_remote_whose_path_is_not_v3_gets_no_button()
    {
        // The translation above is shape-gated. A path on Azure's SSH host that is not
        // exactly v3/org/project/repo cannot be turned into a web address this code can
        // vouch for, and the rule for that is no button, never a guess at a 404.
        var url = Url("git@ssh.dev.azure.com:acme/machine.git");

        Assert.Equal(string.Empty, Forge.CreateUrl(ForgeKind.AzureDevOps, url, "work", "main"));
        Assert.Equal(string.Empty, Forge.ExistingUrl(ForgeKind.AzureDevOps, url, 7));
        Assert.Equal(string.Empty, Forge.ListUrl(ForgeKind.AzureDevOps, url));
    }

    [Fact]
    public void A_host_with_a_port_keeps_it()
    {
        // RemoteUrl drops the port from Host deliberately - a credential is scoped without it -
        // and a link that dropped it too would point at a different service or at nothing.
        var url = Url("https://git.acme.internal:8443/group/machine.git");

        Assert.Equal("git.acme.internal", url.Host);
        Assert.Equal("8443", url.Port);
        Assert.Equal(
            "https://git.acme.internal:8443/group/machine/-/merge_requests/new" +
            "?merge_request[source_branch]=work&merge_request[target_branch]=main",
            Forge.CreateUrl(ForgeKind.GitLab, url, "work", "main"));
    }

    [Fact]
    public void A_host_with_a_path_prefix_keeps_it()
    {
        // A forge served under a sub-path, which is how plenty of them are reverse-proxied.
        Assert.Equal(
            "https://acme.internal/gitlab/group/machine/-/merge_requests/new" +
            "?merge_request[source_branch]=work&merge_request[target_branch]=main",
            Forge.CreateUrl(
                ForgeKind.GitLab, Url("https://acme.internal/gitlab/group/machine.git"),
                "work", "main"));
    }

    [Fact]
    public void A_plain_http_remote_is_not_upgraded_to_https()
    {
        // The self-hosted instance on a private network that never got a certificate. An https
        // link to it simply does not connect.
        Assert.StartsWith(
            "http://git.acme.internal/",
            Forge.CreateUrl(
                ForgeKind.GitHub, Url("http://git.acme.internal/acme/machine.git"), "work", "main"));
    }

    [Fact]
    public void An_ssh_remote_still_produces_an_https_link()
    {
        // The web page is not reached over ssh, and refusing to link an ssh remote would refuse
        // most of them.
        Assert.Equal(
            "https://github.com/acme/machine/pull/7",
            Forge.ExistingUrl(ForgeKind.GitHub, Url("git@github.com:acme/machine.git"), 7));
    }

    [Fact]
    public void A_repository_at_the_host_root_does_not_grow_a_double_slash()
    {
        Assert.Equal(
            "https://git.acme.internal/machine/pulls",
            Forge.ListUrl(ForgeKind.Gitea, Url("https://git.acme.internal/machine.git")));
    }

    // ---- the rule that stops a guess ----------------------------------------------------

    [Fact]
    public void An_unidentifiable_host_yields_no_url_at_all()
    {
        var url = Url("https://git.acme.internal/group/machine.git");

        // MUTATION CHECK: default the unknown host to GitHub's template and every one of these
        // returns a plausible, wrong URL instead of nothing.
        Assert.Equal(string.Empty, Forge.CreateUrl(ForgeKind.Unknown, url, "work", "main"));
        Assert.Equal(string.Empty, Forge.ExistingUrl(ForgeKind.Unknown, url, 7));
        Assert.Equal(string.Empty, Forge.ListUrl(ForgeKind.Unknown, url));
    }

    [Fact]
    public void A_local_remote_yields_no_url_even_when_a_forge_is_configured()
    {
        // Somebody sets forge: GitHub in repo.json and then adds a UNC path as a second remote.
        Assert.Equal(
            string.Empty,
            Forge.CreateUrl(ForgeKind.GitHub, Url(@"\\fileserver\repos\machine.git"), "w", "main"));
    }

    [Fact]
    public void No_target_branch_means_no_create_url()
    {
        // DefaultBranch refuses to guess the trunk, and this must refuse with it rather than
        // produce "compare/...work", which is a real URL that compares against nothing.
        Assert.Equal(
            string.Empty,
            Forge.CreateUrl(ForgeKind.GitHub, Url("https://github.com/a/b.git"), "work", ""));
    }

    // ---- request refs -------------------------------------------------------------------

    [Theory]
    [InlineData(ForgeKind.GitHub, "refs/pull/*/head")]
    [InlineData(ForgeKind.GitLab, "refs/merge-requests/*/head")]
    [InlineData(ForgeKind.AzureDevOps, "")]
    [InlineData(ForgeKind.Bitbucket, "")]
    [InlineData(ForgeKind.Gitea, "")]
    public void Only_the_forges_that_publish_request_refs_are_probed(
        ForgeKind kind, string expected) =>
        Assert.Equal(expected, Forge.RequestRefPattern(kind));

    [Fact]
    public void A_ref_whose_head_is_this_tip_gives_its_number()
    {
        const string output =
            "aaa111\trefs/pull/12/head\n" +
            "bbb222\trefs/pull/13/head\n" +
            "ccc333\trefs/pull/14/head\n";

        Assert.Equal(13, Forge.RequestNumber(output, "bbb222"));
        Assert.Equal(13, Forge.RequestNumber(output, "BBB222"));
    }

    [Fact]
    public void GitLabs_ref_layout_parses_with_the_same_reader()
    {
        Assert.Equal(
            45,
            Forge.RequestNumber("abc\trefs/merge-requests/45/head\n", "abc"));
    }

    [Fact]
    public void No_matching_head_gives_no_number()
    {
        Assert.Null(Forge.RequestNumber("aaa111\trefs/pull/12/head\n", "zzz999"));
        Assert.Null(Forge.RequestNumber(string.Empty, "aaa111"));
        Assert.Null(Forge.RequestNumber(null, "aaa111"));
        Assert.Null(Forge.RequestNumber("aaa111\trefs/pull/12/head\n", null));
    }

    [Fact]
    public void Two_requests_on_one_tip_resolve_to_the_newer()
    {
        // Reopened, or a second request raised from the same commit. Rare, and landing on a
        // real adjacent request beats landing on nothing.
        Assert.Equal(
            31,
            Forge.RequestNumber("aaa\trefs/pull/9/head\naaa\trefs/pull/31/head\n", "aaa"));
    }

    [Fact]
    public void A_ref_that_is_not_a_request_is_ignored()
    {
        Assert.Null(Forge.RequestNumber("aaa\trefs/heads/main\n", "aaa"));
        Assert.Null(Forge.RequestNumber("aaa\trefs/pull/notanumber/head\n", "aaa"));
        Assert.Null(Forge.RequestNumber("garbage with no tab\n", "aaa"));
    }

    [Fact]
    public void Each_forge_is_called_what_its_users_call_it()
    {
        Assert.Equal("merge request", Forge.Noun(ForgeKind.GitLab));
        Assert.Equal("pull request", Forge.Noun(ForgeKind.GitHub));
        Assert.Equal("pull request", Forge.Noun(ForgeKind.AzureDevOps));
        Assert.Equal("pull request", Forge.Noun(ForgeKind.Bitbucket));
        Assert.Equal("pull request", Forge.Noun(ForgeKind.Gitea));
    }
}
