using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.Tracker;

/// <summary>The Android-only part of Tracker's settings sheet: the widget's look, the button that places it, the notification warning.</summary>
public partial class TrackerAndroidSettings : UserControl
{
    private readonly TrackerViewModel? _viewModel;

    /// <summary>For the XAML runtime loader and the designer.</summary>
    public TrackerAndroidSettings() => InitializeComponent();

    public TrackerAndroidSettings(TrackerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        AddWidgetButton.IsVisible = TrackerWidgets.CanRequestPin(Android.App.Application.Context);
    }

    private void OnWidgetStyleChanged(object? sender, EventArgs e) => TrackerWidgets.RefreshAll(Android.App.Application.Context);

    // Checked each time the sheet shows: the user may have changed it in Android settings meanwhile.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is not null) _viewModel.WidgetStyleChanged += OnWidgetStyleChanged;
        NotificationsBlocked.IsVisible = !TrackerReminders.CanNotify(Android.App.Application.Context);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null) _viewModel.WidgetStyleChanged -= OnWidgetStyleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Many launchers hide their widget list; this asks the launcher to place the widget (it confirms).</summary>
    private void AddWidget_Click(object? sender, RoutedEventArgs e) => TrackerWidgets.RequestPin(Android.App.Application.Context);
}
