using Avalonia.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.Scratch.Ui;

/// <summary>Scratch's settings, shown as a sheet over Scratch (see <see cref="ScratchView"/>'s gear).</summary>
public partial class ScratchSettingsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public ScratchSettingsView() => InitializeComponent();

    public ScratchSettingsView(ScratchViewModel viewModel, IModule module, Action close)
    {
        DataContext = viewModel;
        InitializeComponent();
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
}
