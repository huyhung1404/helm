using Helm.Core;
using Helm.Modules.ClaudeChat.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.ClaudeChat;

public static class ClaudeChatServices
{
    public static IServiceCollection AddClaudeChatModule(this IServiceCollection services) =>
        services
            .AddHelmModule<ClaudeChatModule, ClaudeChatPage, ClaudeChatViewModel>()
            .AddSingleton<ClaudeChatView>(); // one view for the whole app: it moves between the page and a floating window
}
