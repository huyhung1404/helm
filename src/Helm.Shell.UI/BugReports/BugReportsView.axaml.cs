using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>
/// The Bug reports page, the same view on Windows, Android, macOS and iOS (<see cref="BugReportsViewModel"/>): the list
/// with its search and filters, and the bug opened (<see cref="BugDetailView"/>) beside it on a wide screen or in its
/// place on a phone. Reporting goes to the platform (<see cref="IBugReportsPageHost.Report"/>: the PC's report window,
/// the phone's report page); the phone's "Shake to report" is in the settings sheet behind the gear.
/// </summary>
public partial class BugReportsView : UserControl
{
    private const double NarrowBelow = 720;
    private readonly BugReportsViewModel? _viewModel;
    private readonly IBugReportsPageHost? _host;
    private bool _narrow;
    private SheetHandle? _settings;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public BugReportsView() => InitializeComponent();

    public BugReportsView(BugReportsViewModel viewModel, IBugReportsPageHost host)
    {
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
        DetailView.Host = host;
        ToolTip.SetTip(ReportButton, $"Report a bug ({host.ReportHint})");
        SettingsButton.IsVisible = host.ShakeSettings is not null;
        MenuRow.IsVisible = host.OpenMenu is not null;
        UpdateEmpty();
    }

    /// <summary>The bug opened in it (the shared detail view).</summary>
    public BugDetailView Detail => DetailView;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is null) return;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Bugs.CollectionChanged += OnBugsChanged;
        _viewModel.Refresh();
        Apply();
        Dispatcher.UIThread.Post(MarkChosen, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Bugs.CollectionChanged -= OnBugsChanged;
        }
        _settings?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _narrow = e.NewSize.Width < NarrowBelow;
        Classes.Set("narrow", _narrow);
        Page.Margin = _narrow ? new Thickness(16, 18, 16, 0) : new Thickness(40, 28, 40, 0);
        // A narrower PC window gives the bug more room.
        Page.ColumnDefinitions = ColumnDefinitions.Parse(_narrow ? "*" : e.NewSize.Width >= 1150 ? "400,*" : "330,*");
        // The filters: wrapped on a wide list, one row that scrolls sideways on a phone.
        FilterScroller.HorizontalScrollBarVisibility = _narrow ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        Apply();
    }

    /// <summary>Wide: list and bug side by side. Narrow: the list, or the bug in its place.</summary>
    private void Apply()
    {
        var open = _viewModel?.Selected is not null;
        Page.ColumnSpacing = _narrow ? 0 : 28;
        Grid.SetColumn(DetailPane, _narrow ? 0 : 1);
        ListPane.IsVisible = !_narrow || !open;
        DetailPane.IsVisible = !_narrow || open;
        // On a phone the opened bug has the whole screen under its Back button.
        Header.IsVisible = !_narrow || !open;
        BackButton.IsVisible = _narrow && open;
        // Nothing opened yet on a wide page: point at the list (not when it is empty).
        PickHint.IsVisible = !_narrow && !open && _viewModel?.HasAnyBug == true;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BugReportsViewModel.Selected):
                var wasShown = DetailPane.IsVisible;
                Apply();
                DetailScroller.Offset = default;
                if (_viewModel?.Selected is not null && (_narrow || !wasShown)) Motion.Enter(DetailPane);
                else if (_narrow) Motion.Enter(ListPane);
                Dispatcher.UIThread.Post(MarkChosen, DispatcherPriority.Loaded);
                break;
            case nameof(BugReportsViewModel.IsEmpty) or nameof(BugReportsViewModel.Filter) or nameof(BugReportsViewModel.HasAnyBug) or nameof(BugReportsViewModel.Query):
                UpdateEmpty();
                Apply();
                break;
        }
    }

    private void OnBugsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(MarkChosen, DispatcherPriority.Loaded);

    /// <summary>The opened bug's card has the strong stroke.</summary>
    private void MarkChosen()
    {
        var id = _viewModel?.Selected?.Id;
        foreach (var card in BugList.GetVisualDescendants().OfType<Border>().Where(b => b.DataContext is BugRowViewModel && b.Classes.Contains("glass")))
            card.Classes.Set("chosen", !_narrow && id is not null && ((BugRowViewModel)card.DataContext!).Id == id);
    }

    /// <summary>An empty list says what to do next: report one, or look under another filter.</summary>
    private void UpdateEmpty()
    {
        if (_viewModel is null) return;
        if (!_viewModel.HasAnyBug)
        {
            EmptyTitle.Text = "No bugs yet";
            EmptyText.Text = $"When something in Helm goes wrong, report it with the button above ({_host?.ReportHint}). It keeps what Helm did in the last minute.";
        }
        else if (_viewModel.Query.Trim().Length > 0)
        {
            EmptyTitle.Text = "Nothing matches";
            EmptyText.Text = "Try another word, or a number such as #B12.";
        }
        else
        {
            EmptyTitle.Text = $"Nothing under “{BugText.Filter(_viewModel.Filter)}”";
            EmptyText.Text = "Pick another filter above, or All.";
        }
    }

    private void Report_Click(object? sender, RoutedEventArgs e) => _host?.Report();

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>The gear: this device's bug report settings as a sheet over the page.</summary>
    public SheetHandle? OpenSettings()
    {
        if (_host?.ShakeSettings is not { } settings) return null;
        _settings?.Close();
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new BugReportsSettingsView(settings, () => sheet?.Close()), 440);
        _settings = sheet;
        return sheet;
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => _viewModel?.CloseDetailCommand.Execute(null);

    /// <summary>Android Back: from a bug to the list. False when the list is already shown.</summary>
    public bool HandleBack()
    {
        if (!_narrow || _viewModel?.Selected is null) return false;
        _viewModel.CloseDetailCommand.Execute(null);
        return true;
    }
}
