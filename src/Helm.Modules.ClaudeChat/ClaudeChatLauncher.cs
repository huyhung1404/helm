using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Modules.ClaudeChat.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.ClaudeChat;

/// <summary>The Quick access tile: straight into the Claude Chat window.</summary>
public sealed class ClaudeChatLauncher(IServiceProvider services, ClaudeChatModule module, IShellNavigation navigation, ILogger<ClaudeChatLauncher> logger) : IModuleLauncher
{
    public string ModuleId => ClaudeChatModule.ModuleId;

    public Task LaunchAsync()
    {
        try
        {
            if (!module.IsEnabled) navigation.ShowPage(typeof(ClaudeChatPage)); // its toggle is there
            else services.GetRequiredService<ChatWindowHost>().Show();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Opening Claude Chat from Quick access failed");
        }
        return Task.CompletedTask;
    }
}
