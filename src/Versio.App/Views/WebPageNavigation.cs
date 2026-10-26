namespace Versio.App.Views;

/// <summary>What the web page view does with a navigation the browser is about to start.</summary>
public enum NavigationDecision
{
    /// <summary>The file this view was asked for, fragment or not: let the browser go.</summary>
    Allow,

    /// <summary>A link the user pressed: cancelled here and handed to the logged link command.</summary>
    Forward,

    /// <summary>The page trying to go somewhere on its own: cancelled, and said in the log.</summary>
    Refuse,
}

/// <summary>
/// The one decision <see cref="WebPageView"/> makes about a navigation, kept pure so the suite
/// can pin it without a WebView2 — which cannot run in there (no desktop, no message pump).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a navigation the user started leaves the pane.</b> The view forwarded every
/// navigation that was not the shown file to the link command, and the link command opens an
/// http(s) or mailto target in the real browser. Script is off, but
/// <c>&lt;meta http-equiv="refresh" content="0;url=https://..."&gt;</c> needs none: a page
/// somebody pushed could launch the user's browser at any address the moment its row was
/// clicked, which is precisely the "repository content is not something the user chose to
/// run" line the view's lock-down draws (the 2026-10-24 review's item 40). The Markdown and
/// ladder pages already only cancel; this view also forwards, so it has to ask who is
/// navigating.
/// </para>
/// <para>
/// A redirect is refused for the same reason even when the chain began with a click: the
/// user chose the link they pressed, not where a server sent it — and only a local file can
/// load here, so a redirect is never one they could have meant.
/// </para>
/// </remarks>
public static class WebPageNavigation
{
    public static NavigationDecision Decide(string uri, string? shown, bool isUserInitiated, bool isRedirected)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // Compared without the fragment, so an in-page "#section" link still scrolls.
        if (shown is not null
            && string.Equals(uri.Split('#', 2)[0], new Uri(shown).AbsoluteUri,
                             StringComparison.OrdinalIgnoreCase))
        {
            return NavigationDecision.Allow;
        }

        return isUserInitiated && !isRedirected
            ? NavigationDecision.Forward
            : NavigationDecision.Refuse;
    }
}
