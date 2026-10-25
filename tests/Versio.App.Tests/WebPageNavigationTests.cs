using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Only a navigation the user started leaves the web page pane.
/// </summary>
/// <remarks>
/// The view cancelled every navigation that was not the shown file and forwarded it to the
/// link command, which opens http(s) and mailto in the real browser. Script is off, but a
/// <c>&lt;meta http-equiv="refresh"&gt;</c> needs none, so a pushed HTML file could launch
/// the user's browser at any address the moment its row was clicked (the 2026-10-24 review's
/// item 40). The browser cannot run in this suite, so the decision is a pure function and
/// this is where it is pinned; <see cref="HtmlPaneFlowTests"/> covers everything around it.
/// </remarks>
public sealed class WebPageNavigationTests
{
    private const string Shown = @"C:\repo\site\index.html";
    private const string ShownUri = "file:///C:/repo/site/index.html";

    [Fact]
    public void The_shown_file_is_allowed_fragment_or_not()
    {
        Assert.Equal(NavigationDecision.Allow, WebPageNavigation.Decide(ShownUri, Shown, isUserInitiated: false, isRedirected: false));
        Assert.Equal(NavigationDecision.Allow, WebPageNavigation.Decide(ShownUri + "#section", Shown, isUserInitiated: true, isRedirected: false));
    }

    [Fact]
    public void A_link_the_user_pressed_is_forwarded()
    {
        Assert.Equal(NavigationDecision.Forward,
            WebPageNavigation.Decide("https://example.com/", Shown, isUserInitiated: true, isRedirected: false));
    }

    [Fact]
    public void A_navigation_the_page_started_itself_is_refused()
    {
        // What a meta refresh looks like to the browser: nobody clicked anything.
        Assert.Equal(NavigationDecision.Refuse,
            WebPageNavigation.Decide("https://example.com/", Shown, isUserInitiated: false, isRedirected: false));
    }

    [Fact]
    public void A_redirect_is_refused_even_after_a_click()
    {
        Assert.Equal(NavigationDecision.Refuse,
            WebPageNavigation.Decide("https://example.com/", Shown, isUserInitiated: true, isRedirected: true));
    }

    [Fact]
    public void Another_local_file_is_only_ever_left_for_by_a_click()
    {
        const string other = "file:///C:/repo/site/other.html";

        Assert.Equal(NavigationDecision.Forward, WebPageNavigation.Decide(other, Shown, isUserInitiated: true, isRedirected: false));
        Assert.Equal(NavigationDecision.Refuse, WebPageNavigation.Decide(other, Shown, isUserInitiated: false, isRedirected: false));
    }

    [Fact]
    public void With_nothing_shown_nothing_is_allowed_through()
    {
        Assert.Equal(NavigationDecision.Refuse,
            WebPageNavigation.Decide(ShownUri, shown: null, isUserInitiated: false, isRedirected: false));
    }
}
