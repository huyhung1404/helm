using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Tracker.Ui;

/// <summary>What each platform does its own way around the shared Tracker pages.</summary>
public interface ITrackerPageHost
{
    /// <summary>Hand the calendar file to the user: a save dialog on desktop, the share sheet on Android.</summary>
    void ExportCalendar(string ics);

    /// <summary>The page came on screen (Android asks for notifications here and checks today's reminder).</summary>
    void Opened();

    /// <summary>
    /// Settings only this platform has (Android: the home-screen widget, the notification warning), as cards in the
    /// settings sheet. Null: none.
    /// </summary>
    Control? CreatePlatformSettings() => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>
/// The Tracker itself, the same view on Windows, Android, macOS and iOS: a glass page with the list's name, the quick
/// add, the tasks, and the other lists; Calendar and Insights replace all of that under the tabs. One layout that
/// narrows for a phone (below 720 px). While the tool is off the lists stay visible but read-only, with a note
/// pointing to the settings.
/// </summary>
public partial class TrackerView : UserControl
{
    private const double Narrow = 720;
    private readonly IModule? _module;
    private readonly TrackerViewModel? _viewModel;
    private readonly ITrackerPageHost? _host;
    private bool _settingsOpen;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public TrackerView() => InitializeComponent();

    public TrackerView(IModule module, TrackerViewModel viewModel, ITrackerPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Confirmations (delete a task or a list) are the same glass sheet on every platform.
        viewModel.UseDialogs(new GlassDialogs(() => this));
        InitializeComponent();
        AttachedToVisualTree += (_, _) => viewModel.Workspaces.CollectionChanged += OnListsChanged;
        DetachedFromVisualTree += (_, _) => viewModel.Workspaces.CollectionChanged -= OnListsChanged;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        MenuRow.IsVisible = _host?.OpenMenu is not null;
        // Tabs beside today's line on a wide page, under it on a phone.
        Grid.SetColumn(Tabs, narrow ? 0 : 1);
        Grid.SetRow(Tabs, narrow ? 1 : 0);
        Tabs.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;
        // Options under the quick add fill the row: on a phone the date has its own line, priority and repeat share the next.
        OptionsGrid.ColumnDefinitions = ColumnDefinitions.Parse(narrow ? "*,*" : "2*,*,*");
        Grid.SetColumnSpan(DueBox, narrow ? 2 : 1);
        Grid.SetColumn(PriorityBox, narrow ? 0 : 1);
        Grid.SetRow(PriorityBox, narrow ? 1 : 0);
        Grid.SetColumn(RepeatBox, narrow ? 1 : 2);
        Grid.SetRow(RepeatBox, narrow ? 1 : 0);
        Grid.SetColumn(DaysBox, narrow ? 0 : 2);
        Grid.SetColumnSpan(DaysBox, narrow ? 2 : 1);
        Grid.SetRow(DaysBox, narrow ? 2 : 1);
        // Tabs on the right, like the other screens, on a phone too (own line under the date).
        Grid.SetColumnSpan(Tabs, narrow ? 2 : 1);
        Tabs.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;        // Lists are added in the settings (the gear); only a phone keeps a quick "New list" at the end of the row.
        NewListButton.IsVisible = narrow;
        if (!narrow) NewListBox.IsVisible = false;
        // First run: the three steps side by side on a wide page, one under the other on a phone.
        FirstRunSteps.Columns = narrow ? 1 : 3;
    }

    /// <summary>Each tab replaces the whole page under the tabs, from its title down.</summary>
    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        // Clicked, arrow keys or a screen reader: whatever checked the tab.
        if (_viewModel is null || sender is not RadioButton { IsChecked: true }) return;
        var insights = ReferenceEquals(sender, InsightsTab);
        if (ReferenceEquals(sender, CalendarTab)) _viewModel.ShowCalendarCommand.Execute(null);
        else _viewModel.ShowListsCommand.Execute(null);
        TasksPanel.IsVisible = !insights;
        InsightsPanel.IsVisible = insights;
        Scroller.Offset = default;
    }

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    /// <summary>Plays the list's way in again for a list just picked (tabs play theirs as they are shown).</summary>
    private static void Replay(Control panel) => Helm.Core.Ui.Motion.Enter(panel);

    /// <summary>The gear: Tracker's settings as a sheet over the page, not a page to navigate to.</summary>
    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _module is null) return;
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new TrackerSettingsView(_viewModel, _module, _host?.CreatePlatformSettings(), () => sheet?.Close()), 480);
        if (sheet is null) return;
        _settingsOpen = true;
        sheet.Closed += () => _settingsOpen = false;
    }

    private void List_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as Control)?.DataContext is not WorkspaceOption list) return;
        _viewModel.SelectedWorkspace = list;
        Scroller.Offset = default;
        Replay(TasksPanel);
    }

    private void NewList_Click(object? sender, RoutedEventArgs e)
    {
        NewListBox.IsVisible = true;
        Dispatcher.UIThread.Post(() => NewListName.Focus(), DispatcherPriority.Input);
    }

    /// <summary>
    /// The flow goes on by itself: a list just made (the first one, or from New list) opens with the cursor in "Add a
    /// task", the next step.
    /// </summary>
    private void OnListsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
        NewListBox.IsVisible = false;
        Replay(TasksPanel);
        // A list made in the settings sheet keeps the cursor there.
        if (!_settingsOpen) Dispatcher.UIThread.Post(() => NewTaskBox.Focus(), DispatcherPriority.Input);
    }

    private void ToggleDone_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.ShowCompleted = !_viewModel.ShowCompleted;
    }

    private void ShareIcs_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _host is null) return;
        _host.ExportCalendar(_viewModel.CalendarIcs());
    }

    // The module outlives the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null) return;
        _module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
        _host?.Opened();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        base.OnDetachedFromVisualTree(e);
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
