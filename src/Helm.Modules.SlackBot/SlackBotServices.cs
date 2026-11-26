using Helm.Core.Mcp;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

public static class SlackBotServices
{
    public static IServiceCollection AddSlackBotModule(this IServiceCollection services) =>
        services
            .AddSingleton<SlackTokens>()
            .AddSingleton<SlackInboxFile>()
            .AddSingleton<ISlackAgent, ClaudeCodeSlackAgent>()
            .AddSingleton<SlackSorter>()
            .AddSingleton<SlackBotEngine>()
            .AddSingleton<ISlackBotShell, SlackBotShell>()
            .AddSingleton<SlackBotInboxViewModel>()
            .AddSingleton<SlackBotOverlay>()
            .AddSingleton<IMcpToolProvider, SlackMcpTools>()
            // AddHelmModule, with the settings view model given the module as the IModule it takes (it is portable).
            .AddSingleton<SlackBotModule>()
            .AddSingleton<IHelmModule>(sp => sp.GetRequiredService<SlackBotModule>())
            .AddSingleton<SlackBotPage>()
            .AddSingleton(sp => ActivatorUtilities.CreateInstance<SlackBotViewModel>(sp, sp.GetRequiredService<SlackBotModule>()))
            .AddSingleton<SlackBotContentPage>();
}
