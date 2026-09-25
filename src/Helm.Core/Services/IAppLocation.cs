namespace Helm.Core.Services;

/// <summary>Where Helm is running from and which path external launchers (startup task, restarts) must use.</summary>
public interface IAppLocation
{
    /// <summary>True when Helm runs from a Velopack installation (not from bin/ or a portable folder).</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// The path that stays valid across updates: the Velopack stub launcher in the install root when installed,
    /// otherwise the running executable. Never the versioned app folder.
    /// </summary>
    string LauncherPath { get; }
}

/// <summary>Non-installed fallback: the running executable.</summary>
public sealed class ProcessAppLocation : IAppLocation
{
    public bool IsInstalled => false;

    public string LauncherPath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Helm.exe");
}
