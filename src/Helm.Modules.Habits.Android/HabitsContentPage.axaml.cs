using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the habits stay visible but read-only, with a note pointing to the settings. Opening the page, or coming
/// back to the app on it, asks for the reason of skipped days. The view model's pop-up (<see cref="HabitsPopup"/>) goes
/// on the overlay layer over the whole screen while the page is shown, so it stays put however far the page is
/// scrolled; it plays the celebrations' confetti.
/// </summary>
public partial class HabitsContentPage : UserControl
{
    private readonly HabitsModule _module;
    private readonly HabitsViewModel _viewModel;
    private readonly IShellNavigation? _navigation;
    private readonly DispatcherTimer _clock;
    private HabitsPopup? _popup;
    private TopLevel? _top;

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
        UpdatePopup();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        HidePopup();
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
        switch (e.PropertyName)
        {
            case nameof(HabitsViewModel.HasPopup) or nameof(HabitsViewModel.ShowLapse):
                Dispatcher.UIThread.Post(UpdatePopup);
                break;
            case nameof(HabitsViewModel.Celebration) when _viewModel.Celebration is not null:
                // After layout, so the confetti knows the pop-up's size.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_viewModel.PlayConfetti) _popup?.PlayConfetti(_viewModel.IsBigCelebration);
                }, DispatcherPriority.Loaded);
                break;
        }
    }

    /// <summary>Puts the pop-up on the overlay layer, sized to the window, or takes it off.</summary>
    private void UpdatePopup()
    {
        if (!_viewModel.HasPopup || VisualRoot is null)
        {
            HidePopup();
            return;
        }
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer || TopLevel.GetTopLevel(this) is not { } top) return;
        _popup ??= new HabitsPopup(_viewModel);
        _popup.Place();
        if (_popup.Parent is null)
        {
            _top = top;
            _top.PropertyChanged += OnTopChanged;
            Fit();
            layer.Children.Add(_popup);
        }
    }

    private void HidePopup()
    {
        if (_top is not null) _top.PropertyChanged -= OnTopChanged;
        _top = null;
        if (_popup?.Parent is Panel panel) panel.Children.Remove(_popup);
    }

    /// <summary>The window turned, or the keyboard took part of it: the pop-up follows.</summary>
    private void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty) Fit();
    }

    private void Fit()
    {
        if (_popup is null || _top is null) return;
        _popup.Width = _top.ClientSize.Width;
        _popup.Height = _top.ClientSize.Height;
    }

    /// <summary>A tap on a card (not on a day of its grid, which takes its own taps, nor on a button): mark today.</summary>
    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled || sender is not Control { DataContext: HabitCard card }) return;
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
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
