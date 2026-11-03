namespace Helm.Modules.Wallet;

// The Bills section of the Money view (its own view model: BillsViewModel).
public sealed partial class WalletViewModel
{
    /// <summary>The recurring bills, this month paid or not; null where the app has none (tests).</summary>
    public BillsViewModel? Bills { get; }
}
