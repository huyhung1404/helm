using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Helm.App.Updates;

/// <summary>
/// Helm is manifested asInvoker (Velopack's Setup/Update run the app with CreateProcess, which fails with
/// ERROR_ELEVATION_REQUIRED for requireAdministrator executables) and elevates itself right after the Velopack
/// bootstrap instead. Result: Helm always runs as administrator, installs/updates still work.
/// </summary>
internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Returns true when the caller should continue (already elevated, or elevation disabled with --no-elevate).
    /// Returns false after starting an elevated copy (or when the user declined UAC); the caller must exit.
    /// </summary>
    public static bool EnsureElevated(string[] args)
    {
        if (IsElevated || args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase)) return true;

        var forwarded = args.Where(a => !a.StartsWith("--veloapp", StringComparison.OrdinalIgnoreCase)).Select(Quote);
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? VelopackLifecycle.MainExe, string.Join(' ', forwarded))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            VelopackLifecycle.HookLog.Write("User declined elevation; Helm did not start.");
        }
        return false;
    }

    private static string Quote(string arg) => arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg}\"" : arg;
}
