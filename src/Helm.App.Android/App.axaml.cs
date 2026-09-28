using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Helm.App.Android.Hosting;
using Helm.Core.Modules;
using Helm.App.Android.Services;
using Helm.App.Android.ViewModels;
using Helm.App.Android.Views;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Helm.App.Android;

public partial class App : Avalonia.Application
{
    private static ServiceProvider? s_services;

    public static IServiceProvider Services => s_services ?? throw new InvalidOperationException("Helm has not started.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The Android process outlives activities; build the services once per process.
        if (s_services is null)
        {
            s_services = AndroidHost.Build();
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };
            _ = StartAsync(s_services);
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
