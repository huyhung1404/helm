using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.ShortcutGuide;

public static class ShortcutGuideServices
{
    public static IServiceCollection AddShortcutGuideModule(this IServiceCollection services) =>
        services
            .AddSingleton<ShortcutGuideEngine>()
            .AddHelmModule<ShortcutGuideModule, ShortcutGuidePage, ShortcutGuideViewModel>()
            .AddSingleton<ShortcutGuideContentPage>();
}
