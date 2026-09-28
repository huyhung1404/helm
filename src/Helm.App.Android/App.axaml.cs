using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Helm.App.Android.Hosting;
using Helm.Core.Modules;
using Helm.App.Android.Services;
using Helm.App.Android.ViewModels;
using Helm.App.Android.Views;
using Helm.Core;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Helm.App.Android;

public partial class App : Avalonia.Application
{
    private static bool s_started;

    /// <summary>The process-wide provider (also used by widgets, which can run without the activity).</summary>
    public static IServiceProvider Services => HelmAndroidServices.Current;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The Android process outlives activities; start once per process. The services themselves may already exist
        // if a widget ran first (AndroidApp.OnCreate configures them).
        HelmAndroidServices.Configure(AndroidHost.Build);
        if (!s_started)
        {
            s_started = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };
            _ = StartAsync(Services);
        }

        var general = Services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId);
        Services.GetRequiredService<ThemeService>().Apply(general.Current.Theme);

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = CreateMainView;
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = CreateMainView();

        base.OnFrameworkInitializationCompleted();
    }

    private static MainView CreateMainView() => new() { DataContext = Services.GetRequiredService<MainViewModel>() };

    /// <summary>Same order as Windows: modules, then background sync, then update checks.</summary>
    private static async Task StartAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<App>>();
        try
        {
            logger.LogInformation("Helm {Version} starting on Android {Release} ({Device})", AppInfo.Version,
                global::Android.OS.Build.VERSION.Release, services.GetRequiredService<IDeviceInfo>().DeviceName);
            await services.GetRequiredService<IModuleHost<IAndroidModule>>().StartAsync(CancellationToken.None).ConfigureAwait(true);
            services.GetRequiredService<SyncEngine>().Start();
            services.GetRequiredService<AndroidUpdateService>().StartBackgroundChecks();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Startup failed");
        }
    }
}
