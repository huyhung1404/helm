using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.Ssh;

/// <summary>SSH's settings (Home → Utilities). Connecting and the terminal are on <see cref="SshContentPage"/>.</summary>
public partial class SshPage : ModulePageBase
{
    private readonly IShellNavigation _navigation;

    public SshPage(SshModule module, SshViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void OpenSsh_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(SshContentPage));
}
