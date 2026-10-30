using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the habits stay visible but read-only, with a note pointing to the settings. Opening the page, or coming
/// back to the app on it, asks for the reason of skipped days; plays the celebrations the view model asks for.
/// </summary>
public partial class HabitsContentPage : UserControl
{
    private readonly HabitsModule _module;
    private readonly HabitsViewModel _viewModel;
    private readonly IShellNavigation? _navigation;
    private readonly DispatcherTimer _clock;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public HabitsContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<HabitsModule>(), HelmAndroidServices.Current.GetRequiredService<HabitsViewModel>(),
            HelmAndroidServices.Current.GetService<IShellNavigation>())
    {
    }

    public HabitsContentPage(HabitsModule module, HabitsViewModel viewModel, IShellNavigation? navigation)
    {
        _module = module;
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // A new day moves today in the grids and the cards back to "to do".
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => viewModel.Refresh());
    }

    // Pages are transient and the module and view model are singletons: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ActivityHost.Resumed += OnResumed;
        ApplyEnabled();
        _clock.Start();
        Shown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock.Stop();
        ActivityHost.Resumed -= OnResumed;
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Back in the app on this page: days ticked elsewhere, and days skipped meanwhile that need a reason.</summary>
    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Shown);

    private void Shown()
    {
        try
        {
            _module.PageShown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Habits page check failed: {ex}");
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HabitsViewModel.Celebration) || _viewModel.Celebration is null) return;
        // After layout, so the card has its size and can be scrolled to.
        Dispatcher.UIThread.Post(() =>
        {
            CelebrationPanel.BringIntoView();
            if (_viewModel.PlayConfetti) Confetti.Play(_viewModel.IsBigCelebration);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A tap on a card (not on a day of its grid, which takes its own taps): tick today.</summary>
    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled || sender is not Control { DataContext: HabitCard card }) return;
        e.Handled = true;
        _viewModel.TapCardCommand.Execute(card);
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(HabitsPage));

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
