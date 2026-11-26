using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.Missions;

/// <summary>Android only: asks the launcher to place the Missions widget (see <see cref="MissionsModule.PinWidget"/>).</summary>
public partial class MissionsWidgetCard : UserControl
{
    private readonly MissionsModule? _module;

    /// <summary>For the XAML runtime loader and the designer.</summary>
    public MissionsWidgetCard() => InitializeComponent();

    public MissionsWidgetCard(MissionsModule module)
    {
        _module = module;
        InitializeComponent();
        // Some launchers cannot place a widget for an app; they still list it in their own widget picker.
        AddWidgetButton.IsVisible = module.CanPinWidget;
    }

    private void AddWidget_Click(object? sender, RoutedEventArgs e) => _module?.PinWidget();
}
