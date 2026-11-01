using Helm.Core;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Keyboard;

public static class KeyboardServices
{
    public static IServiceCollection AddKeyboardModule(this IServiceCollection services)
    {
        // One small record per app, edited as a whole: the latest switch wins.
        services.AddSyncedCollection(new SyncedCollectionOptions<KeyboardAppMode> { Name = KeyboardApps.Collection });
        services.AddSyncGroup("keyboard.", "Helm Keyboard");
        services.AddSingleton<KeyboardApps>();
        services.AddSingleton<KeyboardInstaller>();
        return services.AddHelmModule<KeyboardModule, KeyboardPage, KeyboardViewModel>();
    }
}
