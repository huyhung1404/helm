using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the missions stay visible but read-only, with a note pointing to the settings. Plays the celebrations the
/// view model asks for.
/// </summary>
public partial class MissionsContentPage : UserControl
{
    private readonly MissionsModule _module;
    private readonly MissionsViewModel _viewModel;
    private readonly DispatcherTimer _clock;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public MissionsContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<MissionsModule>(), HelmAndroidServices.Current.GetRequiredService<MissionsViewModel>())
    {
    }

    public MissionsContentPage(MissionsModule module, MissionsViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // "Started 2 hours ago" and the pace go stale; refresh while the page is on screen.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => viewModel.Refresh());
    }

    // Pages are transient and the module and view model are singletons: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ApplyEnabled();
        // Steps done on the PC or by Claude while the app was in the background.
        _viewModel.Refresh();
        _clock.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock.Stop();
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MissionsViewModel.Celebration) || _viewModel.Celebration is null) return;
        // After layout, so the card has its size and can be scrolled to.
        Dispatcher.UIThread.Post(() =>
        {
            CelebrationPanel.BringIntoView();
            if (_viewModel.PlayConfetti) Confetti.Play(_viewModel.IsMissionCelebration);
        }, DispatcherPriority.Loaded);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
