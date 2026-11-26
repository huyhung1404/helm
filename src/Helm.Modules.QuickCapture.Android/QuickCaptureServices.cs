using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.QuickCapture;

public static class QuickCaptureServices
{
    public static IServiceCollection AddQuickCaptureModule(this IServiceCollection services) =>
        services
            .AddAndroidModule<QuickCaptureModule, QuickCapturePage>()
            .AddSingleton<QuickCaptureViewModel>();
}
