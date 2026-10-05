using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    /// <summary>A legend bar's width: its share of the room it has (view glue for the donut's legend).</summary>
    public static IMultiValueConverter ShareWidth { get; } = new ShareWidthConverter();

    private sealed class ShareWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [double share, double width] && width > 0 ? Math.Max(4, share * width) : 4.0;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
    }

    private readonly ConditionalWeakTable<TextBox, string> _amountTexts = [];
    private bool _grouping;

    /// <summary>Thousands separators while an amount is typed, so 1000000 reads 1.000.000; the caret stays put.</summary>
    private void OnAmountTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_grouping || sender is not TextBox box) return;
        var previous = _amountTexts.TryGetValue(box, out var before) ? before : "";
        var (text, caret) = WalletFormat.GroupDigits(box.Text, box.CaretIndex, previous);
        _amountTexts.AddOrUpdate(box, text);
        if (text == box.Text) return;
        _grouping = true;
        try
        {
            box.Text = text;
            box.CaretIndex = caret;
        }
        finally
        {
            _grouping = false;
        }
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
