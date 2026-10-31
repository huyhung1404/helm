using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

/// <summary>Vocabulary's settings (Home → Utilities). The words are on <see cref="VocabContentPage"/>.</summary>
public partial class VocabPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;
    private readonly VocabOverlaySettingsViewModel _overlay;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public VocabPage()
        : this(HelmAndroidServices.Current.GetRequiredService<VocabModule>(), HelmAndroidServices.Current.GetRequiredService<VocabViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>(), HelmAndroidServices.Current.GetRequiredService<VocabOverlaySettingsViewModel>())
    {
    }

    public VocabPage(VocabModule module, VocabViewModel viewModel, IShellNavigation navigation, VocabOverlaySettingsViewModel overlay)
    {
        _navigation = navigation;
        _overlay = overlay;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
        // The pop-up, the bubble and the blocked apps are phone-only.
        OverlayPanel.DataContext = overlay;
    }

    // Pages are transient: listen for the return from the phone's settings only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActivityHost.Resumed += OnResumed;
        _overlay.RefreshPermissions();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Back from "Display over other apps" or "Usage access": what is allowed now.</summary>
    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_overlay.RefreshPermissions);

    private void OpenVocabulary_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(VocabContentPage));

    private void Unblock_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: VocabAppRow row }) _overlay.UnblockCommand.Execute(row);
    }
}
