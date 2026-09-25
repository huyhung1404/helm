using Helm.App.Services;
using Helm.App.ViewModels;
using Helm.App.Views;
using Helm.App.Views.Pages;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Helm.App.Hosting;

internal static class HelmHost
{
    public static IHost Build(HelmPaths paths, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(dispose: false);

        // Velopack-aware location first so Core's TryAdd fallback is skipped.
        builder.Services.AddSingleton<Core.Services.IAppLocation, Updates.VelopackAppLocation>();

        // Core infrastructure (settings, hooks, hotkeys, window/monitor services, module registry)
        builder.Services.AddHelmCore(paths);

        // Modules — each module assembly exposes one registration extension.
        HelmModules.Register(builder.Services);

        // Shell services
        builder.Services.AddSingleton<Core.Services.IUiDispatcher, UiDispatcher>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<ShellNavigator>();
        builder.Services.AddSingleton<IShellNavigator>(sp => sp.GetRequiredService<ShellNavigator>());
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<TrayService>();
        builder.Services.AddSingleton<SearchService>();
        builder.Services.AddSingleton<ShellController>();
        builder.Services.AddSingleton<Updates.VelopackUpdateService>();
        builder.Services.AddSingleton<Core.Services.IUpdateService>(sp => sp.GetRequiredService<Updates.VelopackUpdateService>());

        // Shell views and view models
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<GeneralViewModel>();
        builder.Services.AddSingleton<GeneralPage>();
        builder.Services.AddSingleton<WelcomePage>();
        builder.Services.AddSingleton<WhatsNewPage>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();
        builder.Services.AddSingleton<DiagnosticsPage>();

        return builder.Build();
    }
}
