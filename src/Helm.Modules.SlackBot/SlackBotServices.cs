using Helm.Core;
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
            .AddSingleton<SlackBotInboxViewModel>()
            .AddSingleton<SlackBotOverlay>()
            .AddHelmModule<SlackBotModule, SlackBotPage, SlackBotViewModel>()
            .AddSingleton<SlackBotContentPage>();
}
