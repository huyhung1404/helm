using System.Windows;
using System.Windows.Threading;
using Helm.App.Hosting;
using Helm.App.Services;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Helm.App;

public partial class App : Application
{
    private readonly SingleInstance _instance;
    private readonly string[] _args;
    private IHost? _host;
    private ILogger<App>? _logger;

    internal App(SingleInstance instance, string[] args)
    {
        _instance = instance;
        _args = args;
    }

    // Only used by the WPF-generated Main, which is not the startup object (see Program).
    private App() : this(SingleInstance.Acquire(), []) { }

    internal IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--data-dir <path>" (dev/testing): use a separate settings/logs root instead of %LOCALAPPDATA%\Helm.
        var dataDirIndex = Array.FindIndex(_args, a => a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
        var paths = new HelmPaths(dataDirIndex >= 0 && dataDirIndex + 1 < _args.Length ? _args[dataDirIndex + 1] : null);
        Directory.CreateDirectory(paths.LogsDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "helm-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            _host = HelmHost.Build(paths, _args);
            await _host.StartAsync().ConfigureAwait(true);
            _logger = Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("Helm {Version} starting (elevated: {Elevated})",
                AppInfo.Version, Services.GetRequiredService<Core.Services.IProcessLauncher>().IsElevated);

            var shell = Services.GetRequiredService<ShellController>();
            // "--page X" (dev/testing) always shows the window on that page.
            var startHidden = !_args.Contains("--page", StringComparer.OrdinalIgnoreCase) && (_args.Contains("--startup", StringComparer.OrdinalIgnoreCase)
                || Services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current.StartMinimized);
            shell.Initialize(showWindow: !startHidden);

            _instance.ListenForActivation(() => Dispatcher.BeginInvoke(shell.ShowMainWindow));

            await Services.GetRequiredService<IModuleHost>().StartAsync(CancellationToken.None).ConfigureAwait(true);
            shell.OnModulesStarted();
            // Background sync (a no-op until this device is connected in General → Sync).
            Services.GetRequiredService<Core.Sync.SyncEngine>().Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            System.Windows.MessageBox.Show($"Helm failed to start:\n\n{ex.Message}\n\nSee the log in {paths.LogsDirectory}.", "Helm",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Disables modules, flushes settings, stops the host and shuts WPF down.</summary>
    internal async Task ShutdownGracefullyAsync(int exitCode = 0)
    {
        try
        {
            if (_host is not null)
            {
                await Services.GetRequiredService<IModuleHost>().StopAllAsync().ConfigureAwait(true);
                Services.GetRequiredService<ISettingsStoreFactory>().FlushAll();
                Services.GetRequiredService<TrayService>().Dispose();
                await _host.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                _host.Dispose();
                _host = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during shutdown");
        }
        finally
        {
            _instance.Release();
            Log.Information("Helm exited");
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(exitCode);
        }
    }

    internal void ReleaseSingleInstance() => _instance.Release();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true; // never crash for a UI glitch; it's logged
    }
}
