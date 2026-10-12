using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

public static class WalletServices
{
    public static IServiceCollection AddWalletModule(this IServiceCollection services) =>
        services
            .AddWalletCore()
            .AddWalletLinks(typeof(WalletContentPage))
            .AddHelmModule<WalletModule, WalletPage, WalletViewModel>()
            .AddSingleton<WalletContentPage>();
}
