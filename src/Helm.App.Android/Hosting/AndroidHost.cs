using Helm.Core.Modules;
using Helm.App.Android.Services;
using Helm.App.Android.ViewModels;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Helm.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using AndroidApp = Android.App.Application;

namespace Helm.App.Android.Hosting;

/// <summary>The Android app's services: Helm.Core + Helm.Shell shared with Windows, plus the Android platform pieces.</summary>
internal static class AndroidHost
{
    public static ServiceProvider Build()
    {
        // App-private storage (/data/data/<package>/files/Helm); removed when the app is uninstalled.
        var paths = new HelmPaths(Path.Combine(AndroidApp.Context.FilesDir!.AbsolutePath, "Helm"));
        Directory.CreateDirectory(paths.LogsDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "helm-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            // Information and up also go into the black box (Bug reports), in memory only.
            .WriteTo.Sink(new Helm.Shell.BugReports.BlackBoxSink())
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: false));

        // Shared core (settings, sync) on top of the Android Keystore instead of DPAPI.
        services.AddSingleton<ISecretProtector, AndroidKeystoreProtector>();
        services.AddHelmCommon(paths);
        services.AddSingleton<IModuleHost<IAndroidModule>>(sp => new ModuleRegistry<IAndroidModule>(
            sp.GetServices<IAndroidModule>(), sp.GetRequiredService<ISettingsStoreFactory>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Helm.Core.Modules.ModuleRegistry")));

        // Tools — the Android list (see AndroidModules.cs).
        AndroidModules.Register(services);

        // Helm's old AI records on the sync server: still known to sync, read by nothing.
        services.AddLeftoverAiCollections();

        // Platform services
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IClipboardService, AndroidClipboardService>();
        services.AddSingleton<IDeviceInfo, AndroidDeviceInfo>();
        services.AddSingleton<AndroidLauncher>();
        services.AddSingleton<IProcessLauncher>(sp => sp.GetRequiredService<AndroidLauncher>());
        services.AddSingleton<IFileSharer>(sp => sp.GetRequiredService<AndroidLauncher>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<AndroidUpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<AndroidUpdateService>());
        services.AddSingleton<ShellNavigator>();
        services.AddSingleton<ToolWindows>();
        services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellNavigator>());

        // Shell view models (Updates and Sync are the Windows app's own, from Helm.Shell)
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<BackgroundActivitiesViewModel>();
        services.AddSingleton<SyncIndicatorViewModel>();
        // Home and General are the shared pages (Helm.Shell.UI), filled from the tools and from the view models above.
        services.AddSingleton<Helm.Home.IToolIcons, AndroidToolIcons>();
        services.AddSingleton(sp => new Helm.Home.ShellTools(sp.GetRequiredService<IModuleHost<IAndroidModule>>().Modules, sp.GetRequiredService<Helm.Home.IToolIcons>()));
        services.AddSingleton<Helm.Shell.Home.IHomeLinks, AndroidHomeLinks>();
        services.AddSingleton<Helm.Shell.Home.IGeneralPlatform, AndroidGeneralPlatform>();
        services.AddSingleton<Helm.Core.Today.IHomeAlertSource>(sp => new Helm.Shell.Home.UpdateAlertSource(sp.GetRequiredService<IUpdateService>(), sp.GetRequiredService<Helm.Shell.Home.IHomeLinks>()));
        services.AddSingleton<Helm.Core.Today.IHomeAlertSource>(sp => new Helm.Shell.Home.SyncAlertSource(sp.GetRequiredService<Helm.Core.Sync.SyncEngine>(), sp.GetRequiredService<Helm.Shell.Home.IHomeLinks>()));
        services.AddSingleton(sp => new Helm.Home.HomeScreen(
            sp.GetRequiredService<Helm.Home.ShellTools>(), sp.GetRequiredService<Helm.Home.IToolIcons>(), sp.GetServices<Helm.Core.Today.ITodaySource>(),
            sp.GetServices<Helm.Core.Today.IHomeAlertSource>(), sp.GetServices<Helm.Core.Capture.ICaptureTarget>(), sp.GetRequiredService<Helm.Shell.Home.IHomeLinks>(),
            sp.GetRequiredService<IUiDispatcher>(), sp.GetService<Helm.Core.Sync.SyncEngine>()));
        services.AddSingleton(sp =>
        {
            var composer = new Helm.General.GeneralComposer(
                sp.GetRequiredService<SyncViewModel>(), sp.GetRequiredService<UpdatesViewModel>(), sp.GetRequiredService<BackgroundActivitiesViewModel>(),
                sp.GetRequiredService<Helm.Shell.Home.IGeneralPlatform>(), $"v{AppInfo.Version}", isDesktop: false);
            composer.Model.SetTools(sp.GetRequiredService<Helm.Home.ShellTools>().All);
            return composer;
        });
        services.AddSingleton<MainViewModel>();

        // Bug reports: the black box, the synced bugs, their page and the report page (the app bar, a shake, the tile).
        Helm.Shell.BugReports.BugReportsServices.AddBugReports(services, Helm.Shell.BugReports.BugDeviceKind.Phone);
        services.AddSingleton<BugReports.AndroidBugReporter>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
