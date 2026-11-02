using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet itself, opened from the drawer, the Quick access tile, the widget and the "what was it?" notification (see
/// <see cref="IModuleContent"/>). While the tool is off the transactions stay visible but read-only, with a note
/// pointing to the settings. The floating buttons, the passing notice and the bottom sheets (<see cref="WalletSheet"/>)
/// go on the overlay layer over the whole screen while the page is shown, so they stay put however far it is scrolled.
/// </summary>
public partial class WalletContentPage : UserControl
{
    private readonly WalletModule _module;
    private readonly WalletViewModel _viewModel;
    private WalletSheet? _sheet;
    private TopLevel? _top;

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
        ShowSheet();
        ApplyEnabled();
        // Transactions saved from notifications while the page was away, and "today" after midnight.
        _viewModel.TransactionPager.Reset();
        _viewModel.Refresh();
        _module.PageShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        HideSheet();
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
        if (e.PropertyName == nameof(WalletViewModel.Popup) && _viewModel.HasPopup && _top is not null)
            Dispatcher.UIThread.Post(() => _sheet?.Place(_top?.ClientSize.Height ?? 800));
    }

    /// <summary>Puts the sheet layer on the overlay layer, sized to the window.</summary>
    private void ShowSheet()
    {
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer || TopLevel.GetTopLevel(this) is not { } top) return;
        if (_sheet is null)
        {
            _sheet = new WalletSheet(_viewModel);
            _sheet.AddHandler(TextBox.TextChangedEvent, OnAmountTextChanged);
        }
        if (_sheet.Parent is not null) return;
        _top = top;
        _top.PropertyChanged += OnTopChanged;
        Fit();
        layer.Children.Add(_sheet);
    }

    private void HideSheet()
    {
        if (_top is not null) _top.PropertyChanged -= OnTopChanged;
        _top = null;
        if (_sheet?.Parent is Panel panel) panel.Children.Remove(_sheet);
    }

    /// <summary>The window turned, or the keyboard took part of it: the sheet follows.</summary>
    private void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty) Fit();
    }

    private void Fit()
    {
        if (_sheet is null || _top is null) return;
        _sheet.Width = _top.ClientSize.Width;
        _sheet.Height = _top.ClientSize.Height;
        if (_viewModel.HasPopup) _sheet.Place(_top.ClientSize.Height);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
        _sheet?.SetEnabled(_module.IsEnabled);
        if (!_module.IsEnabled) _viewModel.ClosePopupCommand.Execute(null);
    }
}
