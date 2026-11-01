using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Helm.Core.Modules;
using Helm.Core.Services;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the habits stay visible but read-only, with a note pointing to the settings (Home → Utilities). Coming back to
/// the page (or to the window while it shows) asks for the reason of skipped days. The view model's pop-up
/// (<see cref="HabitsPopup"/>) covers the area the page scrolls in, not the menu, so the rest of Helm stays usable; it
/// plays the celebrations' confetti.
/// </summary>
public partial class HabitsContentPage : Page
{
    private readonly HabitsModule _module;
    private readonly HabitsViewModel _viewModel;
    private readonly IShellNavigation _navigation;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(60) };
    private Window? _window;
    private HabitsPopup? _popup;
    private HabitsPopupAdorner? _adorner;

    public HabitsContentPage(HabitsModule module, HabitsViewModel viewModel, IShellNavigation navigation)
    {
        _module = module;
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        ApplyEnabled();
        // A new day moves today in the grids and the cards back to "to do".
        _clock.Tick += (_, _) => viewModel.Refresh();
        Loaded += (_, _) =>
        {
            viewModel.Refresh();
            viewModel.CheckLapses();
            _clock.Start();
            _window = Window.GetWindow(this);
            if (_window is not null) _window.Activated += OnWindowActivated;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdatePopup);
        };
        Unloaded += (_, _) =>
        {
            HidePopup();
            _clock.Stop();
            if (_window is not null) _window.Activated -= OnWindowActivated;
            _window = null;
        };
    }

    /// <summary>Back at Helm after a while (the page was left open): the days skipped meanwhile need their reason too.</summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _viewModel.Refresh();
        _viewModel.CheckLapses();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HabitsViewModel.HasPopup) or nameof(HabitsViewModel.ShowLapse) or nameof(HabitsViewModel.ShowMark):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdatePopup);
                break;
            case nameof(HabitsViewModel.Celebration) when _viewModel.Celebration is not null:
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (_viewModel.PlayConfetti) _popup?.PlayConfetti(_viewModel.IsBigCelebration);
                });
                break;
        }
    }

    /// <summary>Shows the pop-up over the page's scroll area (or the page itself), or takes it away.</summary>
    private void UpdatePopup()
    {
        if (!_viewModel.HasPopup || !IsLoaded)
        {
            HidePopup();
            return;
        }
        if (_adorner is null)
        {
            UIElement host = FindAncestor<ScrollViewer>(this) ?? (UIElement)this;
            if (AdornerLayer.GetAdornerLayer(host) is not { } layer) return;
            _popup ??= new HabitsPopup(_viewModel);
            _adorner = new HabitsPopupAdorner(host, _popup);
            layer.Add(_adorner);
        }
        _popup?.FocusContent();
    }

    private void HidePopup()
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

    private void Settings_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(HabitsPage));

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
