using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Helm.Modules.ShortcutGuide;

/// <summary>
/// The shortcut library, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>): pick an
/// app, browse its shortcuts, hide the ones you know, add your own. While the tool is off the list stays visible but
/// read-only.
/// </summary>
public partial class ShortcutGuideContentPage : Page
{
    private readonly ShortcutGuideModule _module;
    private readonly ShortcutGuideViewModel _viewModel;

    public ShortcutGuideContentPage(ShortcutGuideModule module, ShortcutGuideViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscription lives as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>View glue: asks before removing an app you added, with its shortcuts.</summary>
    private async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedApp is not { IsOwn: true } app) return;
        var box = new WpfUiMessageBox
        {
            Title = $"Remove {app.Name}?",
            Content = $"{app.Name} and the {app.Shortcuts.Count} shortcuts you added to it are removed from the guide.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            MinWidth = 380,
            Owner = Window.GetWindow(this),
        };
        try
        {
            if (await box.ShowDialogAsync().ConfigureAwait(true) == WpfUiMessageBoxResult.Primary) _viewModel.RemoveSelectedApp();
        }
        catch (InvalidOperationException)
        {
            // The window closed while asking: nothing to remove.
        }
    }
}
