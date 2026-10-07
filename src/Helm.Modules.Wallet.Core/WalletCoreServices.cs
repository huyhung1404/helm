using Helm.Core.Links;
using Helm.Core.Services;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Wallet;

public static class WalletIds
{
    /// <summary>Module id on both apps: the settings file (wallet.json) and the enabled-state key.</summary>
    public const string ModuleId = "wallet";

    public const string DisplayName = "Wallet";

    public const string Description = "Track your spending: the transactions in your bank's notifications (Techcombank, ACB) are saved by themselves; give each a category and see where the money goes. The debt book keeps who owes whom.";
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
        // The debt book: one record per entry, never edited (only settled), so the latest write wins.
        services.AddSyncedCollection(new SyncedCollectionOptions<WalletDebt> { Name = DebtBook.Collection, GuardDeletions = true });
        services.AddSyncGroup("wallet.", WalletIds.DisplayName);
        services.AddSingleton(sp => new WalletStore(
            sp.GetRequiredService<ISyncedCollection<WalletTransaction>>(),
            sp.GetRequiredService<ISyncedCollection<WalletCategory>>(),
            sp.GetRequiredService<ISyncedCollection<WalletBudget>>()));
        services.AddSingleton(sp => new DebtBook(sp.GetRequiredService<ISyncedCollection<WalletDebt>>(), sp.GetRequiredService<WalletStore>()));
        services.AddSingleton<WalletCapture>();
        // Quick Capture: "/s 50k coffee" and "/d Nam 200k".
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget, SpendCaptureTarget>();
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget, DebtCaptureTarget>();
        // The AI agents' tools for the debt book (Helm's MCP server, Windows).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, WalletMcpTools>();
        services.TryAddSingleton(sp => new DebtsViewModel(
            sp.GetRequiredService<DebtBook>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<Helm.Shell.Services.IDialogService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DebtsViewModel>>(),
            sp.GetService<LinkHub>()));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<WalletViewModel>();
        return services;
    }

    /// <summary>
    /// People in the debt book as link targets (notes link to them). The platform passes its Wallet content page, which
    /// opens a linked person.
    /// </summary>
    public static IServiceCollection AddWalletLinks(this IServiceCollection services, Type contentPage) =>
        services.AddHelmLinks().AddSingleton<ILinkProvider>(sp => new DebtLinkProvider(sp.GetRequiredService<DebtBook>(), key =>
        {
            sp.GetRequiredService<IShellNavigation>().ShowPage(contentPage);
            sp.GetRequiredService<WalletViewModel>().ShowPerson(key);
        }));
}
