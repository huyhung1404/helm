using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Workspace;

public static class WorkspaceServices
{
    public static IServiceCollection AddWorkspaceModule(this IServiceCollection services) =>
        services
            .AddWorkspaceSync()
            .AddSingleton<WorkspaceRemoteViewModel>()
            .AddAndroidModule<WorkspaceModule, WorkspacePage>()
            .AddTransient<WorkspaceContentPage>();
}
