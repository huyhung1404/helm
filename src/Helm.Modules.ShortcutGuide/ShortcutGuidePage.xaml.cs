using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.ShortcutGuide;

/// <summary>Shortcut Guide's settings (Home → Utilities). The shortcuts are on <see cref="ShortcutGuideContentPage"/>.</summary>
public partial class ShortcutGuidePage : ModulePageBase
{
    private readonly IShellNavigation _navigation;

    public ShortcutGuidePage(ShortcutGuideViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(ShortcutGuideContentPage));
}
