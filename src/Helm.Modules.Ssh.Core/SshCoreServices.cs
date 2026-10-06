using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Ssh;

public static class SshCoreServices
{
    /// <summary>
    /// The platform-neutral part of SSH: this device's key and the view model. The platform module (Windows, later
    /// Android) calls this from its own <c>AddSshModule()</c>.
    /// </summary>
    public static IServiceCollection AddSshCore(this IServiceCollection services)
    {
        services.AddSingleton(sp => new SshDeviceKey(
            Path.Combine(sp.GetRequiredService<ISettingsStoreFactory>().Paths.ModuleDataDirectory(SshIds.ModuleId), "device-key.bin"),
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetService<IDeviceInfo>()?.DeviceName ?? Environment.MachineName));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<SshViewModel>();
        return services;
    }
}
