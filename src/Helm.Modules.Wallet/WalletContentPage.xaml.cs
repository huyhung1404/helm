using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the transactions stay visible but read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class WalletContentPage : Page
{
    private readonly WalletModule _module;
    private readonly WalletViewModel _viewModel;

    public WalletContentPage(WalletModule module, WalletViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        ApplyEnabled();
        // Transactions the phone saved meanwhile, and "today" after midnight.
        Loaded += (_, _) => viewModel.Refresh();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.IsAddOpen) && _viewModel.IsAddOpen)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => NewAmountBox.Focus());
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        AddButton.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
