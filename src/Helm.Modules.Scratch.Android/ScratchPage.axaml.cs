using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Scratch;

/// <summary>Scratch's settings (Home → Utilities). Scratch is on <see cref="ScratchContentPage"/>.</summary>
public partial class ScratchPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public ScratchPage()
        : this(HelmAndroidServices.Current.GetRequiredService<ScratchModule>(), HelmAndroidServices.Current.GetRequiredService<ScratchViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public ScratchPage(ScratchModule module, ScratchViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
    }

    private void OpenScratch_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(ScratchContentPage));
}
