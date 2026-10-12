using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.QuickCapture;

public static class QuickCaptureServices
{
    public static IServiceCollection AddQuickCaptureModule(this IServiceCollection services) =>
        services
            .AddHelmModule<QuickCaptureModule, QuickCapturePage, QuickCaptureViewModel>();
}
