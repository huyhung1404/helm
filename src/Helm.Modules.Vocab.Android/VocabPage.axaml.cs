using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

/// <summary>Vocabulary's settings (Home → Utilities). The words are on <see cref="VocabContentPage"/>.</summary>
public partial class VocabPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public VocabPage()
        : this(HelmAndroidServices.Current.GetRequiredService<VocabModule>(), HelmAndroidServices.Current.GetRequiredService<VocabViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public VocabPage(VocabModule module, VocabViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
        AddWidgetButton.IsVisible = VocabWidgets.CanRequestPin(Android.App.Application.Context);
        viewModel.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(VocabSettingsViewModel.LockScreenWord)) return;
            var context = Android.App.Application.Context;
            if (viewModel.Settings.LockScreenWord) VocabLockScreen.AskPermissionOnce(context);
            VocabLockScreen.Refresh(context);
        };
    }

    private void OpenVocabulary_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(VocabContentPage));

    /// <summary>Many launchers hide their widget list; this asks the launcher to place the widget (it confirms).</summary>
    private void AddWidget_Click(object? sender, RoutedEventArgs e) => VocabWidgets.RequestPin(Android.App.Application.Context);
}
