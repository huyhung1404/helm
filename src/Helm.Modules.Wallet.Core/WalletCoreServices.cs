using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Wallet;

public static class WalletIds
{
    /// <summary>Module id on both apps: the settings file (wallet.json) and the enabled-state key.</summary>
    public const string ModuleId = "wallet";

    public const string DisplayName = "Wallet";

    public const string Description = "Track your spending: the transactions in your bank's notifications (Techcombank, ACB) are saved by themselves; give each a category and see where the money goes.";
}

public static class WalletCoreServices
{
    /// <summary>
    /// The platform-neutral part of Wallet: its three synced collections, the store, the notification reader and the
    /// page view model. The platform module (Windows or Android) calls this from its own <c>AddWalletModule()</c>.
    /// </summary>
    public static IServiceCollection AddWalletCore(this IServiceCollection services)
    {
        // A transaction is one small record: the latest edit wins. Many deletions at once wait for a confirmation.
        services.AddSyncedCollection(new SyncedCollectionOptions<WalletTransaction>
        {
            Name = WalletStore.TransactionsCollection,
            GuardDeletions = true,
        });
        services.AddSyncedCollection(new SyncedCollectionOptions<WalletCategory> { Name = WalletStore.CategoriesCollection });
        services.AddSyncedCollection(new SyncedCollectionOptions<WalletBudget> { Name = WalletStore.BudgetCollection });
        services.AddSyncGroup("wallet.", WalletIds.DisplayName);
        services.AddSingleton(sp => new WalletStore(
            sp.GetRequiredService<ISyncedCollection<WalletTransaction>>(),
            sp.GetRequiredService<ISyncedCollection<WalletCategory>>(),
            sp.GetRequiredService<ISyncedCollection<WalletBudget>>()));
        services.AddSingleton<WalletCapture>();
        // Quick Capture: "/s 50k coffee".
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget, SpendCaptureTarget>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<WalletViewModel>();
        return services;
    }
}
