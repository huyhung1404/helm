using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Missions.Ui;

/// <summary>
/// Missions itself, the same view on Windows, Android, macOS and iOS: a glass page with a card per mission, the step each
/// running mission is on, and the mission shown (overview, current step, roadmap); statistics and badges under the
/// Insights tab. A new mission or a new plan (<see cref="MissionsNewView"/>) and the celebration
/// (<see cref="MissionsCelebrationView"/>) are sheets that follow the view model; the settings are a sheet behind the
/// gear. While the tool is off the missions stay visible but read only.
/// </summary>
public partial class MissionsView : UserControl
{
    private const double Narrow = 720;
    private readonly IModule? _module;
    private readonly MissionsViewModel? _viewModel;
    private readonly IMissionsPageHost? _host;
    private readonly DispatcherTimer? _clock;
    private SheetHandle? _newSheet;
    private SheetHandle? _celebrationSheet;
    private SheetHandle? _settingsSheet;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public MissionsView() => InitializeComponent();

    public MissionsView(IModule module, MissionsViewModel viewModel, IMissionsPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Skip, abandon and delete ask in the same glass sheet on every platform.
        viewModel.UseDialogs(new GlassDialogs(() => this));
        InitializeComponent();
        // "Started 2 hours ago" and the pace go stale; refresh while the page is on screen.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => viewModel.Refresh());
        _clock.Stop();
    }

    /// <summary>The new mission (or new plan) sheet, while it is open.</summary>
    public SheetHandle? NewSheet => _newSheet;

    /// <summary>The celebration sheet, while it is open.</summary>
    public SheetHandle? CelebrationSheet => _celebrationSheet;

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
        // Today's cards: two side by side on a wide page, one under the other on a phone.
        foreach (var grid in TodayList.GetVisualDescendants().OfType<UniformGrid>()) grid.Columns = narrow ? 1 : 2;
    }

    /// <summary>Each tab replaces the whole page under the tabs, from its title down.</summary>
    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true }) return;
        var insights = ReferenceEquals(sender, InsightsTab);
        MissionPanel.IsVisible = !insights;
        InsightsPanel.IsVisible = insights;
        Scroller.Offset = default;
    }

    /// <summary>Opens the Insights tab (as tapping it does).</summary>
    public void ShowInsights() => InsightsTab.IsChecked = true;

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>A mission's card: show that mission (back on the Missions tab).</summary>
    private void Mission_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Control { DataContext: MissionChoice choice }) return;
        _viewModel.SelectedMission = choice;
    }

    /// <summary>The gear: Missions' settings as a sheet over the page, not a page to navigate to.</summary>
    public SheetHandle? OpenSettings()
    {
        if (_viewModel is null || _module is null) return null;
        SheetHandle? sheet = null;
        var view = new MissionsSettingsView(_viewModel, _module, _host, () => sheet?.Close());
        sheet = Sheet.Show(this, view, 500);
        _settingsSheet = sheet;
        return sheet;
    }

    // The module and view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _viewModel is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Celebrated += OnCelebrated;
        ApplyEnabled();
        _clock?.Start();
        Shown();
        Dispatcher.UIThread.Post(UpdateSheets, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock?.Stop();
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Celebrated -= OnCelebrated;
        }
        CloseSheet(ref _newSheet);
        CloseSheet(ref _celebrationSheet);
        CloseSheet(ref _settingsSheet);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// On screen again (opened, or back at the app on it): steps done on another device or by an AI agent meanwhile.
    /// The platform calls it when the window or the app comes back.
    /// </summary>
    public void Shown()
    {
        if (_viewModel is null) return;
        _viewModel.Refresh();
        try
        {
            _host?.Opened();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Missions page check failed: {ex}");
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Creating a mission closes the flow and may open a celebration in one go: look once both are done.
            case nameof(MissionsViewModel.IsNewOpen) or nameof(MissionsViewModel.HasCelebration):
                Dispatcher.UIThread.Post(UpdateSheets, DispatcherPriority.Loaded);
                break;
            // A celebration that is not from a button here (finished on another device or by an AI agent) still shows.
            case nameof(MissionsViewModel.Celebration) when _viewModel?.PlayConfetti == true:
                var big = _viewModel.IsMissionCelebration;
                Dispatcher.UIThread.Post(() => PlayConfetti(big), DispatcherPriority.Loaded);
                break;
        }
    }

    /// <summary>A step done here: the next step grows into place (phases and missions also get their sheet and confetti).</summary>
    private void OnCelebrated(object? sender, CelebrationKind? kind)
    {
        if (kind is not null || _viewModel?.PlayCelebrations != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (CurrentStep.IsEffectivelyVisible) Motion.Enter(CurrentStep.Card, 0.97);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Opens each sheet while the view model has its flow open, and closes it when the flow ends.</summary>
    private void UpdateSheets()
    {
        if (_viewModel is null) return;
        var vm = _viewModel;
        var onScreen = VisualRoot is not null;

        if (!vm.IsNewOpen || !onScreen) CloseSheet(ref _newSheet);
        else if (_newSheet is not { IsOpen: true } && Sheet.Show(this, new MissionsNewView(vm), 580) is { } sheet)
        {
            _newSheet = sheet;
            // Closed by a tap outside, Escape or Back: the flow ends too (what was typed in the prompt stays).
            sheet.Closed += () =>
            {
                if (ReferenceEquals(_newSheet, sheet)) _newSheet = null;
                if (vm.IsNewOpen) vm.CloseNewCommand.Execute(null);
            };
        }

        // The celebration waits for the new mission sheet (a template finished at once is not a thing, but a sync is).
        if (!vm.HasCelebration || !onScreen) CloseSheet(ref _celebrationSheet);
        else if (_celebrationSheet is not { IsOpen: true } && _newSheet is null
                 && Sheet.Show(this, new MissionsCelebrationView(vm), 440) is { } party)
        {
            _celebrationSheet = party;
            party.Closed += () =>
            {
                if (ReferenceEquals(_celebrationSheet, party)) _celebrationSheet = null;
                if (vm.HasCelebration) vm.DismissCelebrationCommand.Execute(null);
            };
        }
    }

    private static void CloseSheet(ref SheetHandle? sheet)
    {
        var open = sheet;
        sheet = null;
        open?.Close();
    }

    /// <summary>A burst over the whole screen, above the sheet; it clears itself.</summary>
    private void PlayConfetti(bool big)
    {
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer || TopLevel.GetTopLevel(this) is not { } top) return;
        Control screen = this.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Classes.Contains("screen")) ?? top;
        var at = screen.TranslatePoint(default, top) ?? default;
        // Clipped to the screen: in the Gallery's phone frame it must not fall over the rest of the window.
        var confetti = new ConfettiView { Width = screen.Bounds.Width, Height = screen.Bounds.Height, ClipToBounds = true, IsHitTestVisible = false };
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
