using Helm.Core;
using Helm.Core.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Ssh;

public static class SshServices
{
    public static IServiceCollection AddSshModule(this IServiceCollection services) =>
        services
            .AddSshCore()
            .AddHelmModule<SshModule, SshPage, SshViewModel>()
            .AddSingleton<SshContentPage>()
            // The AI agents' tools for the servers' menus: MCP runs on Windows only.
            .AddSingleton<IMcpToolProvider, SshMcpTools>();
}
