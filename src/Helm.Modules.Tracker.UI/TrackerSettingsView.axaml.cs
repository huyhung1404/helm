using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Helm.Core.Modules;

namespace Helm.Modules.Tracker.Ui;

/// <summary>Tracker's settings, shown as a sheet over the Tracker (see <see cref="TrackerView"/>'s gear).</summary>
public partial class TrackerSettingsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public TrackerSettingsView() => InitializeComponent();

    private readonly TrackerViewModel? _viewModel;

    public TrackerSettingsView(TrackerViewModel viewModel, IModule module, Control? platformCards, Action close)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        PlatformCards.Content = platformCards;
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Avalonia.Threading.Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        };
    }

    // A list's name is edited in its row and saved when the field loses focus or on Enter.
    private void Rename_LostFocus(object? sender, RoutedEventArgs e) => Rename(sender as TextBox);

    private void Rename_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Rename(sender as TextBox);
        e.Handled = true;
    }

    private void Rename(TextBox? box)
    {
        if (box?.DataContext is WorkspaceOption list) _viewModel?.RenameWorkspace(list, box.Text ?? "");
    }
}
