using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet itself, opened from the drawer, the Quick access tile, the widget and the "what was it?" notification (see
/// <see cref="IModuleContent"/>). While the tool is off the transactions stay visible but read-only, with a note
/// pointing to the settings.
/// </summary>
public partial class WalletContentPage : UserControl
{
    private readonly WalletModule _module;
    private readonly WalletViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public WalletContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<WalletModule>(), HelmAndroidServices.Current.GetRequiredService<WalletViewModel>())
    {
    }

    public WalletContentPage(WalletModule module, WalletViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // The new transaction's amount and every row's editor: TextChanged bubbles up from the templates.
        AddHandler(TextBox.TextChangedEvent, OnAmountTextChanged);
    }

    private readonly ConditionalWeakTable<TextBox, string> _amountTexts = [];
    private bool _grouping;

    /// <summary>Thousands separators while an amount is typed, so 1000000 reads 1.000.000; the caret stays put.</summary>
    private void OnAmountTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_grouping || e.Source is not TextBox { Text: { } current } box || !box.Classes.Contains("amount")) return;
        var previous = _amountTexts.TryGetValue(box, out var before) ? before : "";
        var (text, caret) = WalletFormat.GroupDigits(current, box.CaretIndex, previous);
        _amountTexts.AddOrUpdate(box, text);
        if (text == current) return;
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

    // Pages are transient and the module and view model are singletons: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ActivityHost.Resumed += OnResumed;
        ApplyEnabled();
        // Transactions saved from notifications while the page was away, and "today" after midnight.
        _viewModel.Refresh();
        _module.PageShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Back from Android's settings (notification access) or from the bank's app.</summary>
    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _viewModel.Refresh();
        _module.PageShown();
    });

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.IsAddOpen) && _viewModel.IsAddOpen)
            Dispatcher.UIThread.Post(() => NewAmountBox.Focus(), DispatcherPriority.Loaded);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        AddButton.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
