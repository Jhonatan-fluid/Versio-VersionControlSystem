using System.Windows;
using Versio.App.Spikes;
using Versio.App.Theme;

namespace Versio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Follow the Windows app theme by default. Dark is the expected setting for this
        // audience, so it is verified on every screen rather than assumed to work.
        ThemeManager.Apply(AppTheme.System);

        // The window is created here rather than via StartupUri, because StartupUri cannot
        // be cleared once set - assigning null to it throws ArgumentNullException.
        var spike = new TreePerformanceSpike();

        // --measure runs the R25 spike, prints its verdict and exits non-zero on failure,
        // so the gate can be checked from a script rather than by looking at a window.
        if (e.Args.Contains("--measure", StringComparer.OrdinalIgnoreCase))
        {
            spike.RunHeadless();
        }

        spike.Show();
    }
}
