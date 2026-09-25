using Helm.Core.Services;

namespace Helm.App.Updates;

/// <summary>Velopack-aware <see cref="IAppLocation"/>: the install-root stub launcher when installed.</summary>
internal sealed class VelopackAppLocation : IAppLocation
{
    public VelopackAppLocation()
    {
        var stable = VelopackLifecycle.StableLauncherPath();
        IsInstalled = stable is not null;
        LauncherPath = stable ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, VelopackLifecycle.MainExe);
    }

    public bool IsInstalled { get; }

    public string LauncherPath { get; }
}
