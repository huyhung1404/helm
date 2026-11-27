using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Helm.App.Hosting;
using Helm.App.Shell;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Helm.App;

/// <summary>
/// Helm on Windows: starts the host (settings, tools, sync, AI workers, MCP), the shell (main window, tray) and the black
/// box, and shuts them down in order on Exit. Closing the main window only hides it.
/// </summary>
public sealed class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    /// <summary>The single-instance guard (Program holds it); released before a restart or on exit.</summary>
    internal static SingleInstance? Instance { get; set; }

    internal static string[] Args { get; set; } = [];

    internal static new App Current => (App)Application.Current!;

    internal IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");

    public override void Initialize() => Styles.Add(new HelmTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        // "--data-dir <path>" (dev/testing): use a separate settings/logs root instead of %LOCALAPPDATA%\Helm.
        var dataDirIndex = Array.FindIndex(Args, a => a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
        var paths = new HelmPaths(dataDirIndex >= 0 && dataDirIndex + 1 < Args.Length ? Args[dataDirIndex + 1] : null);
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
            // Information and up also go into the black box (Bug reports), in memory only.
            .WriteTo.Sink(new Helm.Shell.BugReports.BlackBoxSink())
            .CreateLogger();

        // Never crash for a UI glitch: it is logged.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
            // The black box goes to disk: the next start offers to report the crash with it.
            if (args.IsTerminating) Core.Diagnostics.BlackBox.Shared.WriteCrash(paths.CrashBlackBoxFile, args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            _host = HelmHost.Build(paths, Args);
            await _host.StartAsync().ConfigureAwait(true);
            _logger = Services.GetRequiredService<ILogger<App>>();
            NotificationCard.Services = (Services.GetRequiredService<Core.Desktop.IWindowService>(), Services.GetRequiredService<Core.Desktop.IMonitorService>());
            _logger.LogInformation("Helm {Version} starting (elevated: {Elevated})",
                AppInfo.Version, Services.GetRequiredService<IProcessLauncher>().IsElevated);

            // Workspace Controller is gone: its hooks in Claude Code's settings would call a Helm that no longer answers.
            LegacyClaudeHooks.RemoveInBackground(_logger);

            var shell = Services.GetRequiredService<ShellController>();
            // "--page X" (dev/testing) always shows the window on that page.
            var startHidden = !Args.Contains("--page", StringComparer.OrdinalIgnoreCase) && (Args.Contains("--startup", StringComparer.OrdinalIgnoreCase)
                || Services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current.StartMinimized);
            shell.Initialize(showWindow: !startHidden);
            // Bug reports from anywhere (Ctrl+Alt+F12, the title bar, the tray) and the black box's hooks, before the tools start.
            await Services.GetRequiredService<BugReports.PcBugReporter>().StartAsync().ConfigureAwait(true);

            Instance?.ListenForActivation(() => Dispatcher.UIThread.Post(shell.ShowMainWindow));

            await Services.GetRequiredService<IModuleHost>().StartAsync(CancellationToken.None).ConfigureAwait(true);
            shell.OnModulesStarted();
            // Background sync (a no-op until this device is connected in General → Sync). The live channel stays open
            // while Helm runs, also in the tray: other devices' changes arrive within seconds.
            var engine = Services.GetRequiredService<Core.Sync.SyncEngine>();
            engine.Start();
            engine.SetLive(true);
            // The agents on this PC take the phone's AI jobs through Helm Sync, from launch on; a sign-in or a change of
            // the list tells the phone at once instead of at the next heartbeat.
            var worker = Services.GetRequiredService<Helm.Ai.Assistant.AgentJobWorker>();
            worker.Start();
            // And the user's VPS, when it is set up and on; it hears of the ready agents too.
            var vps = Services.GetRequiredService<Helm.Ai.Vps.VpsWorker>();
            Services.GetRequiredService<Helm.Ai.Vps.VpsWorkerControl>().Apply();
            Services.GetRequiredService<Helm.Ai.Agents.AgentRegistry>().Changed += (_, _) =>
            {
                worker.Beat();
                vps.SendStatus();
            };
            // The keys' model lists are tested again once a day, and the features' prompts come from the user's Skill Hub.
            Services.GetRequiredService<Helm.Ai.AiModelRefresher>().Start();
            Services.GetRequiredService<Helm.Ai.Prompts.AiPromptRefresher>().Start();
            // The AI agents' tools (a pipe only this user can open; each connection checks the MCP settings).
            Services.GetRequiredService<Core.Mcp.McpPipeHost>().Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            await StartupFailed.ShowAsync($"Helm failed to start:\n\n{ex.Message}\n\nSee the log in {paths.LogsDirectory}.").ConfigureAwait(true);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
        }
    }

    /// <summary>Disables the tools, flushes settings, stops the host and ends the app.</summary>
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
            Instance?.Release();
            Log.Information("Helm exited");
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(exitCode);
        }
    }

    internal void ReleaseSingleInstance() => Instance?.Release();

    private static void Shutdown(int exitCode)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exitCode);
        else Environment.Exit(exitCode);
    }
}
