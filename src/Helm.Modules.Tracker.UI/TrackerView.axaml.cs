using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Tracker.Ui;

/// <summary>What each platform does its own way around the shared Tracker page.</summary>
public interface ITrackerPageHost
{
    /// <summary>Hand the calendar file to the user: a save dialog on desktop, the share sheet on Android.</summary>
    void ExportCalendar(string ics);

    /// <summary>The page came on screen (Android asks for notifications here and checks today's reminder).</summary>
    void Opened();
}

/// <summary>
/// The Tracker itself, the same view on Windows, Android, macOS and iOS: a glass page whose first card is the one task
/// to do next, then the quick add, the list, and the other lists. One layout that narrows for a phone (below 720 px the
/// "Up next" card goes under the title and the list cards go two by two). While the tool is off the lists stay visible
/// but read-only, with a note pointing to the settings.
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
        // Title beside "Up next" on a wide page, above it on a phone.
        Head.ColumnDefinitions = ColumnDefinitions.Parse(narrow ? "*" : "*,24,380");
        Grid.SetColumn(Hero, narrow ? 0 : 2);
        Grid.SetRow(Hero, narrow ? 1 : 0);
        Hero.Margin = narrow ? new Thickness(0, 16, 0, 0) : default;
        if (ListCards.ItemsPanelRoot is UniformGrid cards) cards.Columns = narrow ? 2 : Math.Max(2, (int)((e.NewSize.Width - 80) / 250));
    }

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var insights = ReferenceEquals(sender, InsightsTab);
        if (ReferenceEquals(sender, CalendarTab)) _viewModel.ShowCalendarCommand.Execute(null);
        else _viewModel.ShowListsCommand.Execute(null);
        TasksPanel.IsVisible = !insights;
        InsightsPanel.IsVisible = insights;
    }

    private void List_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as Control)?.DataContext is not WorkspaceOption list) return;
        _viewModel.SelectedWorkspace = list;
        Scroller.Offset = default;
    }

    private void NewList_Click(object? sender, RoutedEventArgs e) => NewListBox.IsVisible = true;

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
