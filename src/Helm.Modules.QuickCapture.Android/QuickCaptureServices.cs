using Helm.Ai.Assistant;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.QuickCapture;

public static class QuickCaptureServices
{
    public static IServiceCollection AddQuickCaptureModule(this IServiceCollection services) =>
        services
            .AddAndroidModule<QuickCaptureModule, QuickCapturePage>()
            .AddSingleton<QuickCaptureViewModel>()
            // Ask Helm's other ways to open: the edge handle, its launcher icon and widget, and their settings page.
            .AddSingleton<AskHelmEdgeHandle>()
            .AddSingleton<AskHelmOpeners>()
            .AddSingleton<IAskHelmOpenPlatform, AskHelmOpenPlatform>()
            .AddSingleton<AskHelmWaysViewModel>();
}
