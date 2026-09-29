using Helm.Core;
using Helm.Core.Palette;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.CommandPalette;

public static class CommandPaletteServices
{
    public static IServiceCollection AddCommandPaletteModule(this IServiceCollection services) =>
        services
            .AddHelmModule<CommandPaletteModule, CommandPalettePage, CommandPaletteViewModel>()
            // Windows itself, below Helm's own results (PaletteSearch.ExternalWeight).
            .AddSingleton<IPaletteProvider, AppsPaletteProvider>()
            .AddSingleton<IPaletteProvider, OpenWindowsPaletteProvider>()
            .AddSingleton<IPaletteProvider, WindowsSettingsPaletteProvider>()
            .AddSingleton<IPaletteProvider, WebSearchPaletteProvider>()
            .AddSingleton<ISlowPaletteProvider, FilesPaletteProvider>();
}
