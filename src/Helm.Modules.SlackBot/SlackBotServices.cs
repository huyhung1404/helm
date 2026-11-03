using Helm.Core;
using Helm.Core.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

public static class SlackBotServices
{
    public static IServiceCollection AddSlackBotModule(this IServiceCollection services) =>
        services
            .AddSingleton<SlackTokens>()
            .AddSingleton<SlackInboxFile>()
            .AddSingleton<ISlackAgent, ClaudeCodeSlackAgent>()
            .AddSingleton<ISlackProjects, SlackProjects>()
            .AddSingleton<SlackSorter>()
            .AddSingleton<SlackBotEngine>()
            .AddSingleton<SlackBotInboxViewModel>()
            .AddSingleton<SlackBotOverlay>()
            .AddSingleton<ITelegram, TelegramApi>()
            .AddSingleton<SlackTelegramToken>()
            .AddSingleton<SlackPhoneAlerts>()
            .AddSingleton<IMcpToolProvider, SlackMcpTools>()
            .AddHelmModule<SlackBotModule, SlackBotPage, SlackBotViewModel>()
            .AddSingleton<SlackBotContentPage>();
}
