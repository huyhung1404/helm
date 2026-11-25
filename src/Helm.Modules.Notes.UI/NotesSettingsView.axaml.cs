using Avalonia.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.Notes.Ui;

/// <summary>Notes' settings, shown as a sheet over Notes (see <see cref="NotesView"/>'s gear).</summary>
public partial class NotesSettingsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public NotesSettingsView() => InitializeComponent();

    public NotesSettingsView(NotesViewModel viewModel, IModule module, INotesPageHost? host, Action close)
    {
        DataContext = viewModel;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        ExportCard.IsVisible = host?.CanExport == true;
        ExportButton.Click += (_, _) => host?.Export();
        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Avalonia.Threading.Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        };
    }
}
