using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

/// <summary>Missions' settings (Home → Utilities). The missions themselves are on <see cref="MissionsContentPage"/>.</summary>
public partial class MissionsPage : ModulePageBase
{
    private readonly MissionsModule _module;
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public MissionsPage()
        : this(HelmAndroidServices.Current.GetRequiredService<MissionsModule>(), HelmAndroidServices.Current.GetRequiredService<MissionsViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public MissionsPage(MissionsModule module, MissionsViewModel viewModel, IShellNavigation navigation)
    {
        _module = module;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
        // Some launchers cannot place a widget for an app; they still list it in their own widget picker.
        AddWidgetButton.IsVisible = module.CanPinWidget;
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(MissionsContentPage));

    private void AddWidget_Click(object? sender, RoutedEventArgs e) => _module.PinWidget();
}
