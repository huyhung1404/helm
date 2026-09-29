using Helm.Core;
using Helm.Core.Palette;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.QuickCapture;

public static class QuickCaptureServices
{
    public static IServiceCollection AddQuickCaptureModule(this IServiceCollection services) =>
        services
            .AddHelmModule<QuickCaptureModule, QuickCapturePage, QuickCaptureViewModel>()
            .AddSingleton<IPaletteProvider, QuickCapturePaletteProvider>();
}
