using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Ssh;

/// <summary>SSH's settings (Home → Utilities). Connecting and the terminal are on <see cref="SshContentPage"/>.</summary>
public partial class SshPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public SshPage()
        : this(HelmAndroidServices.Current.GetRequiredService<SshModule>(), HelmAndroidServices.Current.GetRequiredService<SshViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public SshPage(SshModule module, SshViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
        // Back from Vault (unlocked there): the list of Vault fields in the editor is read again.
        AttachedToVisualTree += (_, _) => viewModel.OnSettingsShown();
    }

    private void OpenSsh_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(SshContentPage));
}
