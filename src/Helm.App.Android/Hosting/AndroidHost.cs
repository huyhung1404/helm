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

        // Platform services
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IClipboardService, AndroidClipboardService>();
        services.AddSingleton<IDeviceInfo, AndroidDeviceInfo>();
        services.AddSingleton<AndroidLauncher>();
        services.AddSingleton<IProcessLauncher>(sp => sp.GetRequiredService<AndroidLauncher>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<AndroidUpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<AndroidUpdateService>());
        services.AddSingleton<ShellNavigator>();
        services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellNavigator>());

        // Shell view models (Updates and Sync are the Windows app's own, from Helm.Shell)
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<SyncIndicatorViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<GeneralViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
