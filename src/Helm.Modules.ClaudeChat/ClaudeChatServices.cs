using Helm.Core;
using Helm.Core.Modules;
using Helm.Modules.ClaudeChat.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.ClaudeChat;

public static class ClaudeChatServices
{
    public static IServiceCollection AddClaudeChatModule(this IServiceCollection services) =>
        services
            .AddHelmModule<ClaudeChatModule, ClaudeChatPage, ClaudeChatViewModel>()
            .AddSingleton<ChatWorkspaceViewModel>() // folders and their chats, one Claude Code process per chat
            .AddSingleton<ChatWindowHost>()
            .AddSingleton<IModuleLauncher, ClaudeChatLauncher>(); // Quick access: new chat in a folder you pick
}
