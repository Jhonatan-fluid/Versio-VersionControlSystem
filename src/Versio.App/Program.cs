using Velopack;

namespace Versio.App;

/// <summary>
/// Entry point. Velopack hooks have to run before WPF allocates anything.
/// </summary>
/// <remarks>
/// Velopack relaunches this exe with install / update / uninstall arguments and expects a
/// fast exit. Putting that after <c>App.OnStartup</c> would paint a window during uninstall
/// and hang the installer. The generated WPF Main is replaced by setting StartupObject.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
