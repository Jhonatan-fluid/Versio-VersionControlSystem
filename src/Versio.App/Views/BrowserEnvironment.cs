using System.IO;
using Microsoft.Web.WebView2.Core;
using Versio.Infrastructure.FileSystem;

namespace Versio.App.Views;

/// <summary>
/// The one WebView2 environment the app's page views share.
/// </summary>
/// <remarks>
/// <para>
/// One environment means one browser process tree for the window, however many page views
/// open: the HTML Web page view and the Markdown Rendered view both draw through it. Created
/// on the first request and kept, so the second page opened does not pay the browser's start
/// again.
/// </para>
/// <para>
/// Its profile lives under <c>%LOCALAPPDATA%\VersioUser\WebView2</c>. The default is beside the
/// executable, which is not ours to write to in every install.
/// </para>
/// <para>
/// A failed start is not cached: the runtime can be installed while the app runs, and the next
/// request should find it rather than repeat yesterday's failure.
/// </para>
/// </remarks>
internal static class BrowserEnvironment
{
    private static Task<CoreWebView2Environment>? _starting;

    public static Task<CoreWebView2Environment> GetAsync()
    {
        var starting = _starting ??= Create();
        if (starting.IsFaulted || starting.IsCanceled)
        {
            _starting = null;
            starting = _starting = Create();
        }

        return starting;
    }

    private static async Task<CoreWebView2Environment> Create()
    {
        var profile = Path.Combine(AppUserPaths.Root, "WebView2");
        Directory.CreateDirectory(profile);
        return await CoreWebView2Environment.CreateAsync(null, profile).ConfigureAwait(true);
    }
}
