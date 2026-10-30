using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Helm.Core.Modules;
using Helm.Core.Services;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the habits stay visible but read-only, with a note pointing to the settings (Home → Utilities). Coming back to
/// the page (or to the window while it shows) asks for the reason of skipped days; plays the celebrations the view
/// model asks for.
/// </summary>
public partial class HabitsContentPage : Page
{
    private readonly HabitsModule _module;
    private readonly HabitsViewModel _viewModel;
    private readonly IShellNavigation _navigation;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(60) };
    private Window? _window;

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
        };
        Unloaded += (_, _) =>
        {
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
            case nameof(HabitsViewModel.Celebration) when _viewModel.Celebration is not null:
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    CelebrationPanel.BringIntoView();
                    if (_viewModel.PlayConfetti) Confetti.Play(_viewModel.IsBigCelebration);
                });
                break;
            case nameof(HabitsViewModel.PendingLapse) when _viewModel.PendingLapse is not null:
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ReasonBox.Focus());
                break;
        }
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
