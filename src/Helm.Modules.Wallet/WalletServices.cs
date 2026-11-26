using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

public static class WalletServices
{
    public static IServiceCollection AddWalletModule(this IServiceCollection services) =>
        services
            .AddWalletCore()
            .AddWalletLinks(navigation => navigation.ShowTool(WalletIds.ModuleId))
            .AddHelmModule<WalletModule, WalletViewModel>()
            .AddSingleton<IGoalPhotos, WalletGoalPhotos>();
}
