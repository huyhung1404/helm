using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the transactions stay visible but read-only, with a note pointing to the settings (Home → Utilities). The view
/// model's pop-up and passing notices (<see cref="WalletOverlay"/>) cover the area the page scrolls in, not the menu, so
/// the rest of Helm stays usable.
/// </summary>
public partial class WalletContentPage : Page
{
    private readonly WalletModule _module;
    private readonly WalletViewModel _viewModel;
    private WalletOverlay? _overlay;
    private WalletOverlayAdorner? _adorner;

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
        Loaded += (_, _) =>
        {
            viewModel.Refresh();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateOverlay);
        };
        Unloaded += (_, _) => HideOverlay();
    }

    /// <summary>A legend bar's width: its share of the room it has (view glue for the donut's legend).</summary>
    public static IMultiValueConverter ShareWidth { get; } = new ShareWidthConverter();

    private sealed class ShareWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [double share, double width] && width > 0 ? Math.Max(4, share * width) : 4.0;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.Popup) or nameof(WalletViewModel.HasToast))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateOverlay);
    }

    /// <summary>Shows the overlay over the page's scroll area while there is a pop-up or a notice, or takes it away.</summary>
    private void UpdateOverlay()
    {
        if (!_viewModel.HasPopup && !_viewModel.HasToast || !IsLoaded)
        {
            HideOverlay();
            return;
        }
        if (_adorner is null)
        {
            UIElement host = FindAncestor<ScrollViewer>(this) ?? (UIElement)this;
            if (AdornerLayer.GetAdornerLayer(host) is not { } layer) return;
            _overlay ??= new WalletOverlay(_viewModel);
            _adorner = new WalletOverlayAdorner(host, _overlay);
            layer.Add(_adorner);
        }
        // A notice alone lets clicks through to the page.
        _adorner.IsHitTestVisible = _viewModel.HasPopup;
        _overlay?.Place();
    }

    private void HideOverlay()
    {
        if (_adorner is null) return;
        AdornerLayer.GetAdornerLayer(_adorner.AdornedElement)?.Remove(_adorner);
        _adorner = null;
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(child); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is T match) return match;
        }
        return null;
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
        ToCategorizeButton.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (!on) _viewModel.ClosePopupCommand.Execute(null);
    }
}
