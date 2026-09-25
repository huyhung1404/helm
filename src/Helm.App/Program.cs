using Helm.App.Updates;
using Velopack;

namespace Helm.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 1. Velopack first (vpk checks it is literally in Main): install/update/uninstall hooks exit inside Run().
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(VelopackLifecycle.AutoApplyOnStartup())
            .OnFirstRun(VelopackLifecycle.FirstRun)
            .OnRestarted(VelopackLifecycle.Restarted)
            .OnAfterUpdateFastCallback(VelopackLifecycle.AfterUpdate)
            .OnBeforeUninstallFastCallback(VelopackLifecycle.BeforeUninstall)
            .Run();

        // 2. Helm needs administrator rights; relaunch elevated (UAC) when started unelevated.
        if (!Elevation.EnsureElevated(args)) return 0;

        // 3. Single instance: a second launch activates the first one.
        using var instance = SingleInstance.Acquire();
        // --allow-multiple (dev/testing): run next to an installed Helm instead of activating it.
        if (!instance.IsFirstInstance && !args.Contains("--allow-multiple", StringComparer.OrdinalIgnoreCase))
        {
            instance.SignalFirstInstance();
            return 0;
        }

        var app = new App(instance, args);
        app.InitializeComponent();
        return app.Run();
    }
}
