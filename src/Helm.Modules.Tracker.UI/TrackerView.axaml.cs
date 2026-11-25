using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Tracker.Ui;

/// <summary>What each platform does its own way around the shared Tracker pages.</summary>
public interface ITrackerPageHost
{
    /// <summary>Hand the calendar file to the user: a save dialog on desktop, the share sheet on Android.</summary>
    void ExportCalendar(string ics);

    /// <summary>The page came on screen (Android asks for notifications here and checks today's reminder).</summary>
    void Opened();

    /// <summary>Show Tracker's settings page (the gear at the top of the Tracker).</summary>
    void OpenSettings();
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

    /// <summary>For the XAML loader and the previewer only.</summary>
    public TrackerView() => InitializeComponent();

    public TrackerView(IModule module, TrackerViewModel viewModel, ITrackerPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
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
        // Tabs beside today's line on a wide page, under it on a phone.
        Grid.SetColumn(Tabs, narrow ? 0 : 1);
        Grid.SetRow(Tabs, narrow ? 1 : 0);
        Tabs.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;
        // First run: the three steps side by side on a wide page, one under the other on a phone.
        FirstRunSteps.Columns = narrow ? 1 : 3;
        FirstRunButton.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
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

    /// <summary>Plays the list's way in again for a list just picked (tabs play theirs as they are shown).</summary>
    private static void Replay(Control panel) => Helm.Core.Ui.Motion.Enter(panel);

    private void Settings_Click(object? sender, RoutedEventArgs e) => _host?.OpenSettings();

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
        Dispatcher.UIThread.Post(() => NewTaskBox.Focus(), DispatcherPriority.Input);
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
