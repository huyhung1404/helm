using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Helm.App.Android.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.Views;

public partial class MainView : UserControl
{
    private IInsetsManager? _insets;
    private TopLevel? _topLevel;

    private ShellNavigator? _navigator;
    private MainViewModel? _viewModel;
    private readonly DrawerController _drawer;

    public MainView()
    {
        InitializeComponent();
        _drawer = new DrawerController(Drawer, (TranslateTransform)Drawer.RenderTransform!, DrawerScrim, () => _viewModel);
        SheetScrim.PointerPressed += (_, _) => _viewModel?.HideSheetCommand.Execute(null);
        DataContextChanged += (_, _) =>
        {
            if (_navigator is not null) _navigator.PropertyChanged -= OnNavigatorChanged;
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as MainViewModel;
            _navigator = _viewModel?.Navigator;
            if (_navigator is not null) _navigator.PropertyChanged += OnNavigatorChanged;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
            ShowActiveTab();
        };
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPaneOpen) && _viewModel is { } vm) _drawer.Show(vm.IsPaneOpen);
    }

    private void OnNavigatorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellNavigator.ActiveTab)) ShowActiveTab();
    }

    /// <summary>
    /// Shows the active page's view, built the first time and kept while the page is kept (<see cref="ShellNavigator"/>),
    /// so it keeps its state (scroll position, an open item) while other pages are shown. Only the shown page is in the
    /// visual tree: a hidden page's timers and live updates pause as they did when a page was left. A page built again
    /// after it was let go scrolls back to where it was.
    /// </summary>
    private void ShowActiveTab()
    {
        if (_navigator?.ActiveTab is not { } tab)
        {
            PageArea.Child = null;
            return;
        }
        if (tab.View is null)
        {
            tab.View = PageViews.Build(tab);
            if (tab.View is ScrollViewer scroller && _navigator.TakeScrollOffset(tab.Key) is { } offset)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => scroller.Offset = offset, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        if (ReferenceEquals(PageArea.Child, tab.View)) return;
        PageArea.Child = null;
        // A view outlives its window when Android makes the activity again (the tabs are the app's, not the window's).
        if (tab.View.Parent is Decorator old) old.Child = null;
        PageArea.Child = tab.View;
        // A page that lays itself out (fixed bars, its own scrolling) gets the whole area, without the app bar.
        AppBar.IsVisible = !PageViews.IsFull(tab);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null) return;

        // Android 15+ always draws edge to edge; pad the shell by the status/navigation bars and display cutout.
        _insets = _topLevel.InsetsManager;
        if (_insets is not null)
        {
            _insets.DisplayEdgeToEdgePreference = true;
            UpdateSystemBarColor();
        }
        // A full page (no app bar) has its own menu button; it opens the drawer.
        Helm.Core.Ui.ShellChrome.SetNavigation(_topLevel, _ =>
        {
            if (DataContext is MainViewModel vm) vm.IsPaneOpen = true;
        });
        ContentInsets.Changed += OnContentInsetsChanged;
        UpdatePadding();
        ActualThemeVariantChanged += OnThemeChanged;
        _topLevel.BackRequested += OnBackRequested;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ContentInsets.Changed -= OnContentInsetsChanged;
        if (_topLevel is not null) _topLevel.BackRequested -= OnBackRequested;
        ActualThemeVariantChanged -= OnThemeChanged;
        _insets = null;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateSystemBarColor();

    /// <summary>Status/navigation bars take the app bar colour (Avalonia picks light or dark bar icons to match).</summary>
    private void UpdateSystemBarColor()
    {
        if (_insets is not null && this.TryFindResource("HelmBarBackground", ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid)
            _insets.SystemBarColor = solid.Color;
    }

    private void OnContentInsetsChanged(object? sender, EventArgs e) => UpdatePadding();

    /// <summary>
    /// Pads the shell clear of the status/navigation bars and cutout, once. The amount comes from Android itself (see
    /// <see cref="ContentInsets"/>), not from Avalonia's safe area, which could be read before edge to edge took effect.
    /// </summary>
    private void UpdatePadding() => Padding = ContentInsets.Current;

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.HandleBack()) e.Handled = true;
    }
}
