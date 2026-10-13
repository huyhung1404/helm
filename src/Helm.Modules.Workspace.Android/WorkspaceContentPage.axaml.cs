using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Workspace;

/// <summary>
/// The control screen, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). It fills
/// the page area itself (<see cref="IFullPage"/>): fixed bars, the timeline scrolling between them, the composer kept
/// above the keyboard. While the tool is off on this phone it stays readable.
/// </summary>
public partial class WorkspaceContentPage : UserControl, IFullPage
{
    private readonly WorkspaceModule _module;
    private readonly WorkspaceRemoteViewModel _viewModel;
    private readonly EdgeHints _projectEdges;
    private readonly EdgeHints _sessionEdges;
    private TopLevel? _topLevel;
    private bool _stickToEnd = true;
    private System.Collections.ObjectModel.ObservableCollection<TimelineRow> _timeline = [];

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public WorkspaceContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<WorkspaceModule>(), HelmAndroidServices.Current.GetRequiredService<WorkspaceRemoteViewModel>())
    {
    }

    public WorkspaceContentPage(WorkspaceModule module, WorkspaceRemoteViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _projectEdges = new EdgeHints(ProjectScroller, ProjectStrip, ProjectLeft, ProjectRight);
        _sessionEdges = new EdgeHints(SessionScroller, SessionStrip, SessionLeft, SessionRight);
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
        ChatScroller.ScrollChanged += OnChatScrolled;
        Composer.SizeChanged += (_, e) => FitChips(e.NewSize.Width);
    }

    /// <summary>A narrow phone keeps the composer's row in one line: the session time goes first, then the mode's name (its icon stays).</summary>
    private void FitChips(double width)
    {
        AgeChip.IsVisible = width >= 420;
        ModeText.IsVisible = width >= 380;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Opened from a notification: straight to that conversation.
        if (Helm.Core.Platform.ActivityHost.Latest?.Intent is { } intent && intent.GetStringExtra(WorkspaceWatchService.ExtraSession) is { Length: > 0 } session)
        {
            var machine = intent.GetStringExtra(WorkspaceWatchService.ExtraMachine) ?? "";
            intent.RemoveExtra(WorkspaceWatchService.ExtraSession);
            intent.RemoveExtra(WorkspaceWatchService.ExtraMachine);
            _viewModel.OpenSession(machine, session);
        }
        _viewModel.PropertyChanged += OnViewModelChanged;
        _timeline = _viewModel.Timeline;
        _timeline.CollectionChanged += OnTimelineChanged;
        _viewModel.Projects.CollectionChanged += OnTabsChanged;
        _viewModel.SessionTabs.CollectionChanged += OnTabsChanged;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel?.InputPane is { } pane) pane.StateChanged += OnKeyboardChanged;
        ClearNotification();
        Dispatcher.UIThread.Post(() =>
        {
            KeepTabsInView();
            ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Pages are transient: a new one is made each time the tool opens.
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _timeline.CollectionChanged -= OnTimelineChanged;
        _viewModel.Projects.CollectionChanged -= OnTabsChanged;
        _viewModel.SessionTabs.CollectionChanged -= OnTabsChanged;
        if (_topLevel?.InputPane is { } pane) pane.StateChanged -= OnKeyboardChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceRemoteViewModel.Timeline))
        {
            // A new list for another conversation: follow it.
            _timeline.CollectionChanged -= OnTimelineChanged;
            _timeline = _viewModel.Timeline;
            _timeline.CollectionChanged += OnTimelineChanged;
            _stickToEnd = true;
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
        }
        if (e.PropertyName == nameof(WorkspaceRemoteViewModel.OpenSessionId))
        {
            ClearNotification();
            // Another conversation: start at its end.
            _stickToEnd = true;
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
        }
    }

    /// <summary>The open conversation's notification is read now, but only while Helm is in front: the page stays
    /// attached in the background, where a refresh must not remove a notification nobody saw.</summary>
    private void ClearNotification()
    {
        if (Helm.Core.Platform.ActivityHost.Current is null) return;
        if (_viewModel.OpenSessionId is { } id) WorkspaceWatchService.Cancel(global::Android.App.Application.Context, id);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(KeepTabsInView, DispatcherPriority.Background);

    private void KeepTabsInView()
    {
        _projectEdges.KeepSelectedInView();
        _sessionEdges.KeepSelectedInView();
    }

    /// <summary>New steps keep the end in sight, unless you scrolled up to read.</summary>
    private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_stickToEnd) Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnChatScrolled(object? sender, ScrollChangedEventArgs e)
    {
        // Only your own scrolling decides; the content growing under you does not.
        if (e.ExtentDelta.Y != 0 && e.OffsetDelta.Y == 0) return;
        _stickToEnd = ChatScroller.Offset.Y + ChatScroller.Viewport.Height >= ChatScroller.Extent.Height - 48;
    }

    private void ScrollToEnd() => ChatScroller.ScrollToEnd();

    /// <summary>The keyboard covers the bottom of the screen: the composer moves up above it.</summary>
    private void OnKeyboardChanged(object? sender, Avalonia.Controls.Platform.InputPaneStateEventArgs e)
    {
        if (_topLevel is null) return;
        var covered = e.EndRect.Height;
        var bottom = this.TranslatePoint(new Point(0, Bounds.Height), _topLevel) is { } p ? _topLevel.Bounds.Height - p.Y : 0;
        Root.Margin = new Thickness(0, 0, 0, Math.Max(0, covered - bottom));
        if (covered > 0) Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void Slash_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Prompt.Length == 0) _viewModel.Prompt = "/";
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text?.Length ?? 0;
    }

    private void Menu_Click(object? sender, RoutedEventArgs e) => ShellChrome.OpenNavigation?.Invoke();

    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e) => _viewModel.CloseSheetCommand.Execute(null);

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled() => OffBar.IsVisible = !_module.IsEnabled;
}

/// <summary>A sheet takes at most four fifths of the screen.</summary>
public sealed class SheetHeight : IValueConverter
{
    public static readonly SheetHeight Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double h && h > 0 ? h * 0.8 : double.PositiveInfinity;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
