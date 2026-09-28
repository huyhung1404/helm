using Helm.App.Updates;
using Helm.Core.Services;
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
        // --allow-multiple (dev/testing): run next to an installed Helm, outside its single-instance group.
        var allowMultiple = args.Contains("--allow-multiple", StringComparer.OrdinalIgnoreCase);
        using var instance = allowMultiple ? SingleInstance.Detached() : SingleInstance.Acquire();
        if (!allowMultiple && !instance.IsFirstInstance)
        {
            instance.SignalFirstInstance();
            return 0;
        }

        var app = new App(instance, args);
        app.InitializeComponent();
        return app.Run();
    }
}
