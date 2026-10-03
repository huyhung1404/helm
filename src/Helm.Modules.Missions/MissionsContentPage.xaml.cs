using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the missions stay visible but read-only, with a note pointing to the settings (Home → Utilities). Plays the
/// celebrations the view model asks for.
/// </summary>
public partial class MissionsContentPage : Page
{
    private readonly MissionsModule _module;
    private readonly MissionsViewModel _viewModel;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(60) };

    public MissionsContentPage(MissionsModule module, MissionsViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Celebrated += (_, kind) => Dispatcher.BeginInvoke(() => Celebrate(kind));
        ApplyEnabled();
        // "Started 2 hours ago" and the pace go stale; refresh while the page is on screen.
        _clock.Tick += (_, _) => viewModel.Refresh();
        Loaded += (_, _) =>
        {
            viewModel.Refresh();
            _clock.Start();
        };
        Unloaded += (_, _) => _clock.Stop();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // A celebration that is not from a button here (finished on another device or by Claude) still shows.
            case nameof(MissionsViewModel.Celebration) when _viewModel.Celebration is not null:
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    CelebrationPanel.BringIntoView();
                    if (_viewModel.PlayConfetti) Confetti.Play(_viewModel.IsMissionCelebration);
                });
                break;
            case nameof(MissionsViewModel.IsImportOpen) when _viewModel.IsImportOpen:
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ImportBox.Focus());
                break;
        }
    }

    /// <summary>A step done here: a short pulse on the overview (phases and missions also get the card and confetti).</summary>
    private void Celebrate(CelebrationKind? kind)
    {
        if (kind is not null || !_viewModel.PlayCelebrations) return;
        var pulse = new DoubleAnimation(1, 1.02, TimeSpan.FromMilliseconds(140)) { AutoReverse = true, EasingFunction = new QuadraticEase() };
        OverviewScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
        OverviewScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
    }

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
