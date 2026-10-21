using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.WindowZones;

/// <summary>Window Zones' settings (Home → Utilities). The zones are on <see cref="WindowZonesContentPage"/>.</summary>
public partial class WindowZonesPage : ModulePageBase
{
    private readonly IShellNavigation _navigation;

    public WindowZonesPage(WindowZonesViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OpenZones_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(WindowZonesContentPage));
}
