using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Missions.Ui;

/// <summary>Missions' settings, shown as a sheet over the page (see <see cref="MissionsView"/>'s gear).</summary>
public partial class MissionsSettingsView : UserControl
{
    private const double TwoColumns = 400;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public MissionsSettingsView() => InitializeComponent();

    public MissionsSettingsView(MissionsViewModel viewModel, IModule module, IMissionsPageHost? host, Action close)
    {
        DataContext = viewModel;
        InitializeComponent();
        PlatformCards.Content = host?.CreatePlatformSettings();
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        ExportButton.Content = host?.ExportLabel ?? "Export CSV…";
        ExportButton.IsEnabled = host is not null;
        ExportButton.Click += (_, _) => host?.ExportCsv(viewModel.Csv());
        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        }
        AttachedToVisualTree += (_, _) => module.PropertyChanged += OnModule;
        DetachedFromVisualTree += (_, _) => module.PropertyChanged -= OnModule;
    }

    /// <summary>Reward and deadline side by side on a wide sheet, one under the other on a phone.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < TwoColumns;
        Grid.SetRow(DeadlineField, narrow ? 1 : 0);
        Grid.SetColumn(DeadlineField, narrow ? 0 : 1);
        RewardGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        RewardGrid.RowSpacing = narrow ? 12 : 0;
    }

    // A one-line field is saved when it loses focus, or on Enter.
    private void Field_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
