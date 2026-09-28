using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Services;

public sealed class ProcessLauncher(ILogger<ProcessLauncher> logger) : IProcessLauncher
{
    public string ExecutablePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Helm.exe");

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open folder {Path}", path);
        }
    }

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Url}", url);
        }
    }

    public void StartNewInstance(string? arguments = null)
    {
        Process.Start(new ProcessStartInfo(ExecutablePath, arguments ?? string.Empty)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath),
        });
    }
}
