using System.IO;
using System.Windows;
using Versio.App.Spikes;
using Versio.App.Theme;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;

namespace Versio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Follow the Windows app theme by default. Dark is the expected setting for this
        // audience, so it is verified on every screen rather than assumed to work.
        ThemeManager.Apply(AppTheme.System);

        // --spike runs the R25 tree measurement instead of the shell. Kept in the shipped
        // app rather than deleted, so the virtualization settings have a regression check
        // that anyone can run.
        if (e.Args.Contains("--spike", StringComparer.OrdinalIgnoreCase))
        {
            var spike = new TreePerformanceSpike();
            if (e.Args.Contains("--measure", StringComparer.OrdinalIgnoreCase))
            {
                spike.RunHeadless();
            }

            spike.Show();
            return;
        }

        var clock = new SystemClock();
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(clock).AddSink(sink);

        // The file sink needs a repository, so it is attached when one is opened. Until
        // then the ring buffer holds everything and the console shows it.
        var log = new LogViewModel(sink);
        var shell = new ShellViewModel(logger, log);

        new MainWindow(shell).Show();

        // Open a repository straight away when one is obvious: an explicit path argument,
        // otherwise the working directory. Starting on an empty window and making the user
        // hunt for a folder picker is a poor first thirty seconds.
        var candidate = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                        ?? Directory.GetCurrentDirectory();

        if (Directory.Exists(candidate))
        {
            _ = shell.OpenAsync(candidate);
        }
    }
}
