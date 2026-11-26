using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.QuickCapture;

public static class QuickCaptureServices
{
    public static IServiceCollection AddQuickCaptureModule(this IServiceCollection services) =>
        services
            .AddSingleton<IQuickCapture>(sp => sp.GetRequiredService<QuickCaptureModule>())
            .AddHelmModule<QuickCaptureModule, QuickCaptureViewModel>();
}
