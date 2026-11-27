using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Wallet.Ui;

/// <summary>The tabs of the Wallet page.</summary>
public enum WalletTab
{
    Money,
    Debts,
    Goals,
    Insights,
}

/// <summary>
/// Wallet itself, the same view on Windows, Android, macOS and iOS: a glass page with today's line, the tabs (Money,
/// Debts, Goals, Insights) and the gear. Each tab is its own view (<see cref="MoneyView"/>, <see cref="DebtsView"/>,
/// <see cref="GoalsView"/>, <see cref="InsightsView"/>). Every pop-up of the view model is one sheet whose content
/// follows it (<see cref="WalletPopupView"/>): a tap outside, Escape or Back goes one step back, as Cancel does. The
/// settings are a sheet behind the gear (<see cref="WalletSettingsView"/>). While the tool is off the page stays
/// visible but read only.
/// </summary>
public partial class WalletView : UserControl
{
    private const double Narrow = 720;
    private readonly IModule? _module;
    private readonly WalletViewModel? _viewModel;
    private readonly IWalletPageHost? _host;
    private readonly MoneyView? _money;
    private readonly DebtsView? _debts;
    private readonly GoalsView? _goals;
    private readonly InsightsView? _insights;
    private SheetHandle? _popup;
    private SheetHandle? _settings;
    private WalletTab _tab = WalletTab.Money;
    private bool _syncingTabs;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public WalletView() => InitializeComponent();

    public WalletView(IModule module, WalletViewModel viewModel, IWalletPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Deleting asks in the same glass sheet on every platform.
        var dialogs = new GlassDialogs(() => this);
        viewModel.UseDialogs(dialogs);
        InitializeComponent();
        _money = new MoneyView(viewModel);
        if (viewModel.Debts is { } debts) _debts = new DebtsView(debts);
        if (viewModel.Goals is { } goals) _goals = new GoalsView(goals);
        _insights = new InsightsView(viewModel);
        TabHost.Content = _money;
    }

    /// <summary>The tab shown.</summary>
    public WalletTab Tab => _tab;

    /// <summary>The sheet over the page (a transaction, a form, a fund…), while it is open.</summary>
    public SheetHandle? Popup => _popup;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        MenuRow.IsVisible = _host?.OpenMenu is not null;
        // Tabs and gear beside today's line on a wide page, on their own line under it on a phone, on the left (as on every page).
        Grid.SetColumn(Tabs, narrow ? 0 : 1);
        Grid.SetRow(Tabs, narrow ? 1 : 0);
        Grid.SetColumnSpan(Tabs, narrow ? 2 : 1);
        Tabs.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;
        Tabs.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    }

    /// <summary>Each tab replaces the whole page under the tabs, from its title down, and scrolls to the top.</summary>
    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncingTabs || sender is not RadioButton { IsChecked: true } button) return;
        ShowTab(ReferenceEquals(button, DebtsTab) ? WalletTab.Debts
            : ReferenceEquals(button, GoalsTab) ? WalletTab.Goals
            : ReferenceEquals(button, InsightsTab) ? WalletTab.Insights
            : WalletTab.Money);
    }

    /// <summary>Opens a tab (as tapping it does).</summary>
    public void ShowTab(WalletTab tab)
    {
        if (_viewModel is null) return;
        if (tab == WalletTab.Debts && _debts is null || tab == WalletTab.Goals && _goals is null) tab = WalletTab.Money;
        switch (tab)
        {
            case WalletTab.Debts:
                _viewModel.ShowDebtsCommand.Execute(null);
                break;
            case WalletTab.Goals:
                _viewModel.ShowGoalsCommand.Execute(null);
                break;
            default:
                _viewModel.ShowMoneyCommand.Execute(null);
                break;
        }
        Show(tab);
    }

    private void Show(WalletTab tab)
    {
        var changed = tab != _tab;
        _tab = tab;
        _syncingTabs = true;
        (tab switch { WalletTab.Debts => DebtsTab, WalletTab.Goals => GoalsTab, WalletTab.Insights => InsightsTab, _ => MoneyTab }).IsChecked = true;
        _syncingTabs = false;
        Control? view = tab switch { WalletTab.Debts => _debts, WalletTab.Goals => _goals, WalletTab.Insights => _insights, _ => _money };
        if (!ReferenceEquals(TabHost.Content, view))
        {
            TabHost.Content = view;
            if (view is not null) Motion.Enter(view);
        }
        if (changed) Scroller.Offset = default;
    }

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>The gear: Wallet's settings as a sheet over the page, not a page to navigate to.</summary>
    public SheetHandle? OpenSettings()
    {
        if (_viewModel is null || _module is null || _host is null) return null;
        SheetHandle? sheet = null;
        var view = new WalletSettingsView(_viewModel, _module, _host, () => sheet?.Close());
        sheet = Sheet.Show(this, view, 520);
        _settings = sheet;
        return sheet;
    }

    // The module and view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _viewModel is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.ToCategorize.CollectionChanged += OnToCategorizeChanged;
        ApplyEnabled();
        SyncTab();
        Shown();
        Dispatcher.UIThread.Post(UpdatePopup, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.ToCategorize.CollectionChanged -= OnToCategorizeChanged;
        }
        ClosePopup();
        // The settings sheet goes with the page (another page in its place must not open under it).
        _settings?.Close();
        _settings = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// On screen again (opened, or back at the app on it): transactions the phone saved meanwhile, "today" after
    /// midnight. The platform calls it when the window or the app comes back.
    /// </summary>
    public void Shown()
    {
        if (_viewModel is null) return;
        _viewModel.TransactionPager.Reset();
        _viewModel.Refresh();
        UpdateTodayLine();
        try
        {
            _host?.Opened();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Wallet page check failed: {ex}");
        }
    }

    private void OnToCategorizeChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateTodayLine);

    /// <summary>"Thursday 26 November · 3 to categorize".</summary>
    private void UpdateTodayLine()
    {
        if (_viewModel is null) return;
        TodayLine.Text = _viewModel.HasToCategorize ? _viewModel.ToCategorizeTitle.ToUpper(CultureInfo.CurrentCulture) : "";
        TodayLine.IsVisible = TodayLine.Text.Length > 0;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // One pop-up closing as another opens (an editor back to its details): look once both are done.
            case nameof(WalletViewModel.Popup):
                Dispatcher.UIThread.Post(UpdatePopup, DispatcherPriority.Loaded);
                break;
            // The view model moved to the debts or the goals (a transaction's "Open", the Goal widget).
            case nameof(WalletViewModel.IsDebtsView) or nameof(WalletViewModel.IsGoalsView):
                Dispatcher.UIThread.Post(SyncTab);
                break;
            case nameof(WalletViewModel.ToCategorizeTitle):
                Dispatcher.UIThread.Post(UpdateTodayLine);
                break;
        }
    }

    /// <summary>The tab the view model wants (Insights stays while it is on the money).</summary>
    private void SyncTab()
    {
        if (_viewModel is null) return;
        var tab = _viewModel.IsDebtsView ? WalletTab.Debts : _viewModel.IsGoalsView ? WalletTab.Goals : _tab == WalletTab.Insights ? WalletTab.Insights : WalletTab.Money;
        if (tab != _tab || TabHost.Content is null) Show(tab);
    }

    /// <summary>Opens the sheet while the view model has a pop-up, closes it when it has none.</summary>
    private void UpdatePopup()
    {
        if (_viewModel is null) return;
        if (_viewModel.Popup == WalletPopup.Charts)
        {
            // Every chart has its own tab now.
            _viewModel.ClosePopupCommand.Execute(null);
            ShowTab(WalletTab.Insights);
            return;
        }
        if (!_viewModel.HasPopup || VisualRoot is null)
        {
            ClosePopup();
            return;
        }
        if (_popup is { IsOpen: true }) return;
        var vm = _viewModel;
        // A tap outside, Escape and Back go one step back, as Cancel and the X do; the sheet closes when none is left.
        var handle = Sheet.Show(this, new WalletPopupView(vm, Sheet.IsPhone(this)), 500, canClose: () =>
        {
            if (vm.HasPopup) vm.DismissPopupCommand.Execute(null);
            return !vm.HasPopup;
        });
        if (handle is null) return;
        _popup = handle;
        handle.Closed += () =>
        {
            if (ReferenceEquals(_popup, handle)) _popup = null;
            if (vm.HasPopup) vm.ClosePopupCommand.Execute(null);
        };
    }

    private void ClosePopup()
    {
        var popup = _popup;
        _popup = null;
        popup?.Close();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
        if (!_module.IsEnabled) _viewModel?.ClosePopupCommand.Execute(null);
    }
}
