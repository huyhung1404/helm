using Avalonia.Controls;

namespace Helm.Modules.Tracker.Ui;

/// <summary>Tracker's settings, shown as a sheet over the Tracker (see <see cref="TrackerView"/>'s gear).</summary>
public partial class TrackerSettingsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public TrackerSettingsView() => InitializeComponent();

    public TrackerSettingsView(TrackerViewModel viewModel, Action close)
    {
        DataContext = viewModel;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
    }
}
