using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.Scratch;

/// <summary>Scratch's settings (Home → Utilities). Scratch is on <see cref="ScratchContentPage"/>.</summary>
public partial class ScratchPage : ModulePageBase
{
    private readonly IShellNavigation _navigation;

    public ScratchPage(ScratchModule module, ScratchViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void OpenScratch_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(ScratchContentPage));
}
