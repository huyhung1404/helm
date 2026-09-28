using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

/// <summary>Tracker's settings (Home → Utilities). The lists are on <see cref="TrackerContentPage"/>.</summary>
public partial class TrackerPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public TrackerPage()
        : this(HelmAndroidServices.Current.GetRequiredService<TrackerModule>(), HelmAndroidServices.Current.GetRequiredService<TrackerViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public TrackerPage(TrackerModule module, TrackerViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
        WidgetSection.IsVisible = TrackerWidgets.CanRequestPin(Android.App.Application.Context);
    }

    // Checked each time the page shows: the user may have changed it in Android settings meanwhile.
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        NotificationsBlocked.IsVisible = !TrackerReminders.CanNotify(Android.App.Application.Context);
    }

    private void OpenTracker_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(TrackerContentPage));

    /// <summary>Many launchers hide their widget list; this asks the launcher to place the widget (it confirms).</summary>
    private void AddWidget_Click(object? sender, RoutedEventArgs e) => TrackerWidgets.RequestPin(Android.App.Application.Context);
}
