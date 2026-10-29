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
        // The elevated helper: this same exe, re-launched with the runas verb by
        // LocalGroups.JoinElevatedAsync to add the account to a Windows group a vendor gates
        // its API behind. One job, one result file, exit — before WPF, which would otherwise
        // open a full Versio window ELEVATED (Velopack acts only on its own --veloapp-*
        // arguments and would pass these through). The parent logs the outcome; this process
        // has no logger of its own because it may be running as a different account than the
        // one whose log it would write. ExeJoinBranchTests starts the built exe to prove it.
        if (args.Length > 0
            && args[0] == Versio.Infrastructure.Platform.LocalGroups.JoinArgument
            && OperatingSystem.IsWindows())
        {
            Environment.Exit(Versio.Infrastructure.Platform.LocalGroups.RunJoin(args));
        }

        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
