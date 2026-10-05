using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

public static class WalletServices
{
    public static IServiceCollection AddWalletModule(this IServiceCollection services) =>
        services
            .AddWalletCore()
            .AddWalletLinks(typeof(WalletContentPage))
            .AddSingleton<IWalletPlatform, AndroidWalletPlatform>()
            .AddAndroidModule<WalletModule, WalletPage>()
            .AddTransient<WalletContentPage>();
}
