using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Habits.Ui;

/// <summary>What each platform does its own way around the shared Habits page.</summary>
public interface IHabitsPageHost
{
    /// <summary>The page came on screen, or the app came back to it (Android asks for notifications and checks today's reminder).</summary>
    void Opened();

    /// <summary>Settings only this platform has, as cards in the settings sheet. Null: none.</summary>
    Control? CreatePlatformSettings() => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>
/// Habits itself, the same view on Windows, Android, macOS and iOS: a glass page with today's habits (each a card with
/// its year), the ones done today folded under them, and the report under its tab. Marking a day, the celebration and
/// the reason for skipped days are one sheet (<see cref="HabitsPopupView"/>) that follows the view model's pop-up;
/// the habits are managed in the settings sheet (the gear). While the tool is off the habits stay visible but read only.
/// </summary>
public partial class HabitsView : UserControl
{
    private const double Narrow = 720;
    private readonly IModule? _module;
    private readonly HabitsViewModel? _viewModel;
    private readonly IHabitsPageHost? _host;
    private readonly DispatcherTimer? _clock;
    private SheetHandle? _popup;
    private bool _doneOpen = true;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public HabitsView() => InitializeComponent();

    public HabitsView(IModule module, HabitsViewModel viewModel, IHabitsPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Deleting a habit asks in the same glass sheet on every platform.
        viewModel.UseDialogs(new GlassDialogs(() => this));
        InitializeComponent();
        // A new day moves today in the grids and the cards back to "to do".
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => viewModel.Refresh());
        _clock.Stop();
    }

    /// <summary>The sheet over the page (marking a day, the celebration, a reason), while it is open.</summary>
    public SheetHandle? Popup => _popup;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        MenuRow.IsVisible = _host?.OpenMenu is not null;
        // Tabs and gear beside today's line on a wide page, on their own line under it on a phone (still on the right).
        Grid.SetColumn(Tabs, narrow ? 0 : 1);
        Grid.SetRow(Tabs, narrow ? 1 : 0);
        Grid.SetColumnSpan(Tabs, narrow ? 2 : 1);
        Tabs.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;
        Tabs.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        FirstRunSteps.Columns = narrow ? 1 : 3;
        StatGrid.Columns = narrow ? 2 : 4;
    }

    /// <summary>Each tab replaces the whole page under the tabs, from its title down.</summary>
    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not RadioButton { IsChecked: true }) return;
        var report = ReferenceEquals(sender, ReportTab);
        if (report) _viewModel.ShowReportCommand.Execute(null);
        else _viewModel.ShowHabitsCommand.Execute(null);
        HabitsPanel.IsVisible = !report;
        ReportPanel.IsVisible = report;
        Scroller.Offset = default;
    }

    /// <summary>Opens the Report tab (as tapping it does).</summary>
    public void ShowReport() => ReportTab.IsChecked = true;

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings(focusAdd: false);

    /// <summary>First run: the settings sheet with the cursor in "Name a habit", the one step to take.</summary>
    private void AddFirst_Click(object? sender, RoutedEventArgs e) => OpenSettings(focusAdd: true);

    /// <summary>The gear: Habits' settings as a sheet over the page, not a page to navigate to.</summary>
    public SheetHandle? OpenSettings(bool focusAdd)
    {
        if (_viewModel is null || _module is null) return null;
        SheetHandle? sheet = null;
        var view = new HabitsSettingsView(_viewModel, _module, _host?.CreatePlatformSettings(), () => sheet?.Close());
        sheet = Sheet.Show(this, view, 480);
        if (focusAdd) Dispatcher.UIThread.Post(view.FocusNewHabit, DispatcherPriority.Input);
        return sheet;
    }

    private void ToggleDone_Click(object? sender, RoutedEventArgs e)
    {
        _doneOpen = !_doneOpen;
        Motion.SetTurn(DoneChevron, _doneOpen);
        DoneList.IsVisible = _doneOpen;
    }

    // The module and view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _viewModel is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ApplyEnabled();
        _clock?.Start();
        Shown();
        Dispatcher.UIThread.Post(UpdatePopup, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock?.Stop();
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        ClosePopup();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// On screen again (opened, or back at the app on it): days ticked elsewhere, and days skipped meanwhile that need
    /// their reason. The platform calls it when the window or the app comes back.
    /// </summary>
    public void Shown()
    {
        if (_viewModel is null) return;
        _viewModel.Refresh();
        // Off is read only: nothing to answer until it is on again.
        if (_module?.IsEnabled != false) _viewModel.CheckLapses();
        try
        {
            _host?.Opened();
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
            // Saving a mark closes it and opens the celebration in one go: look once both are done.
            case nameof(HabitsViewModel.HasPopup):
                Dispatcher.UIThread.Post(UpdatePopup, DispatcherPriority.Loaded);
                break;
            case nameof(HabitsViewModel.Celebration) when _viewModel?.PlayConfetti == true:
                // After layout, so the confetti knows the screen's size.
                Dispatcher.UIThread.Post(() => PlayConfetti(_viewModel.IsBigCelebration), DispatcherPriority.Loaded);
                break;
        }
    }

    /// <summary>Opens the sheet while the view model has a pop-up, closes it when it has none.</summary>
    private void UpdatePopup()
    {
        if (_viewModel is null) return;
        if (!_viewModel.HasPopup || VisualRoot is null)
        {
            ClosePopup();
            return;
        }
        if (_popup is { IsOpen: true }) return;
        var vm = _viewModel;
        var handle = Sheet.Show(this, new HabitsPopupView(vm), 440, canClose: () => vm.CanDismissPopup);
        if (handle is null) return;
        _popup = handle;
        // Closed by a tap outside, Escape or Back: what it showed is dismissed too.
        handle.Closed += () =>
        {
            if (ReferenceEquals(_popup, handle)) _popup = null;
            if (vm.HasPopup && vm.CanDismissPopup) vm.DismissPopupCommand.Execute(null);
        };
    }

    private void ClosePopup()
    {
        var popup = _popup;
        _popup = null;
        popup?.Close();
    }

    /// <summary>A burst over the whole screen, above the sheet; it clears itself.</summary>
    private void PlayConfetti(bool big)
    {
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer || TopLevel.GetTopLevel(this) is not { } top) return;
        Control screen = this.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Classes.Contains("screen")) ?? top;
        var at = screen.TranslatePoint(default, top) ?? default;
        // Clipped to the screen: in the Gallery's phone frame it must not fall over the rest of the window.
        var confetti = new ConfettiView { Width = screen.Bounds.Width, Height = screen.Bounds.Height, ClipToBounds = true };
        Canvas.SetLeft(confetti, at.X);
        Canvas.SetTop(confetti, at.Y);
        layer.Children.Add(confetti);
        Dispatcher.UIThread.Post(() => confetti.Play(big), DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(() => layer.Children.Remove(confetti), TimeSpan.FromSeconds(4));
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
    }
}
