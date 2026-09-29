using Helm.Core;
using Helm.Core.Palette;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.CommandPalette;

public static class CommandPaletteServices
{
    public static IServiceCollection AddCommandPaletteModule(this IServiceCollection services) =>
        services
            .AddHelmModule<CommandPaletteModule, CommandPalettePage, CommandPaletteViewModel>()
            .AddSingleton<IPaletteProvider, AppsPaletteProvider>();
}
