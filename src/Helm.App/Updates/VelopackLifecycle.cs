using System.Diagnostics;
using System.Text.Json;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;

namespace Helm.App.Updates;

/// <summary>
/// Velopack bootstrap. Must run first in Main — before the single-instance mutex and the DI host — because
/// install/update/uninstall invoke Helm.exe with special arguments and expect it to handle them and exit quickly
/// (fast callbacks: no UI, 15–30 s budget). Nothing here may depend on the host or WPF.
/// </summary>
internal static class VelopackLifecycle
{
    public const string PackId = "HelmApp";   // install root %LOCALAPPDATA%\HelmApp — must differ from Helm's data folder
    public const string MainExe = "Helm.exe";

    /// <summary>Only apply a pre-downloaded update at startup when the user opted in.</summary>
    public static bool AutoApplyOnStartup() => ReadGeneralSettings()?.Updates.AutoInstallOnRestart ?? false;

    public static void FirstRun(SemanticVersion version)
    {
        HookLog.Write($"First run of {version} after install.");
        // Reinstalling over an older build keeps the same shortcut paths; drop Explorer's cached icons.
        try { Core.Desktop.ShellNotifications.RefreshIcons(); }
        catch (Exception ex) { HookLog.Write($"Icon refresh failed: {ex.Message}"); }
    }

    public static void Restarted(SemanticVersion version) => HookLog.Write($"Restarted after updating to {version}.");

    /// <summary>
    /// The launcher path that survives updates: the stub Helm.exe in the install root
    /// (never current\ or a versioned folder). Null when not installed.
    /// </summary>
    public static string? StableLauncherPath()
    {
        try
        {
            var locator = VelopackLocator.Current;
            if (locator.CurrentlyInstalledVersion is null || string.IsNullOrEmpty(locator.RootAppDir)) return null;
            var stub = Path.Combine(locator.RootAppDir, MainExe);
            return File.Exists(stub) ? stub : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void AfterUpdate(SemanticVersion version)
    {
        HookLog.Write($"Updated to {version}.");
        // Shortcut paths do not change across updates, so Explorer keeps showing the old cached icon otherwise.
        try { Core.Desktop.ShellNotifications.RefreshIcons(); }
        catch (Exception ex) { HookLog.Write($"Icon refresh failed: {ex.Message}"); }
        // Keep an existing startup task pointing at the stable launcher (it is not created if the user never enabled it).
        TryFixStartupTask();
    }

    public static void BeforeUninstall(SemanticVersion version)
    {
        HookLog.Write($"Uninstalling {version}.");
        RemoveStartupTask();
        RemoveTrayRegistrations();

        var general = ReadGeneralSettings();
        if (general?.PurgeDataOnUninstall == true)
        {
            try
            {
                var root = new HelmPaths().Root;
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                HookLog.Write($"Could not purge data: {ex.Message}");
            }
        }
    }

    private static void TryFixStartupTask()
    {
        if (StableLauncherPath() is not { } launcher) return;
        try
        {
            var service = new StartupTaskService(NullLogger<StartupTaskService>.Instance);
            if (service.EnsurePathAsync(launcher).GetAwaiter().GetResult()) HookLog.Write($"Startup task re-pointed to {launcher}.");
        }
        catch (Exception ex)
        {
            // Hooks may run unelevated; Helm re-checks the task path on every elevated start as well.
            HookLog.Write($"Could not update the startup task here ({ex.Message}); Helm will fix it on next start.");
        }
    }

    private static void RemoveStartupTask()
    {
        try
        {
            new StartupTaskService(NullLogger<StartupTaskService>.Instance).DisableAsync().GetAwaiter().GetResult();
            HookLog.Write("Startup task removed.");
        }
        catch (Exception ex)
        {
            // The task was created elevated; an unelevated uninstaller may not delete it. Ask Windows to do it
            // with elevation (fire-and-forget; fast callbacks must not block on UI).
            HookLog.Write($"Startup task removal needs elevation ({ex.Message}); requesting it.");
            try
            {
                Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{StartupTaskService.TaskName}\" /F")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception inner)
            {
                HookLog.Write($"Elevated removal was not possible: {inner.Message}");
            }
        }
    }

    /// <summary>Removes Helm's entries from the taskbar "notification area icons" list (HKCU\Control Panel\NotifyIconSettings).</summary>
    private static void RemoveTrayRegistrations()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var path = key?.GetValue("ExecutablePath") as string;
                if (path is null) continue;
                if (path.Contains($@"\{PackId}\", StringComparison.OrdinalIgnoreCase) && path.EndsWith(MainExe, StringComparison.OrdinalIgnoreCase))
                {
                    root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                    HookLog.Write($"Removed tray registration {name}.");
                }
            }
        }
        catch (Exception ex)
        {
            HookLog.Write($"Could not clean tray registrations: {ex.Message}");
        }
    }

    /// <summary>Reads general.json without the settings infrastructure (hooks run before anything else exists).</summary>
    private static GeneralSettings? ReadGeneralSettings()
    {
        try
        {
            var path = new HelmPaths().SettingsFile(GeneralSettings.StoreId);
            return File.Exists(path) ? JsonSerializer.Deserialize<GeneralSettings>(File.ReadAllText(path), HelmJson.Options) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static class HookLog
    {
        public static void Write(string message)
        {
            try
            {
                var dir = new HelmPaths().LogsDirectory;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "velopack-hooks.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Logging must never break an install/uninstall.
            }
        }
    }
}
