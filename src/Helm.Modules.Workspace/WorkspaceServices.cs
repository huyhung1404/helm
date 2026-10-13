using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Workspace;

public static class WorkspaceServices
{
    public static IServiceCollection AddWorkspaceModule(this IServiceCollection services) =>
        services
            .AddSingleton<IPromptKeyboard, WindowsPromptKeyboard>()
            .AddSingleton<PromptSender>()
            .AddSingleton<BridgeInstaller>()
            .AddHelmModule<WorkspaceModule, WorkspacePage, WorkspaceViewModel>()
            .AddSingleton<WorkspaceContentPage>();
}
