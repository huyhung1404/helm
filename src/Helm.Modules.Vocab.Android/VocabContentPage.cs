using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Ui;
using Helm.Modules.Vocab.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the
/// shared <see cref="VocabView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>). Its
/// settings are the sheet behind the gear, with the phone's own cards (the bubble, the apps the pop-up stays out of) added
/// through <see cref="IVocabPageHost"/>. Back goes through the view (a sheet, a word's page, root practice); "Open in Helm"
/// on the pop-up card opens that word.
/// </summary>
public sealed class VocabContentPage : UserControl, IFullPage, IVocabPageHost
{
    private readonly VocabModule _module;
    private readonly VocabViewModel _viewModel;
    private readonly VocabOverlaySettingsViewModel _overlay;
    private readonly VocabView _view;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public VocabContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<VocabModule>(), HelmAndroidServices.Current.GetRequiredService<VocabViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<VocabOverlaySettingsViewModel>())
    {
    }

    public VocabContentPage(VocabModule module, VocabViewModel viewModel, VocabOverlaySettingsViewModel overlay)
    {
        _module = module;
        _viewModel = viewModel;
        _overlay = overlay;
        _view = new VocabView(module, viewModel, this);
        Content = _view;
    }


    public string AddHint => "Several at once with commas. From any app: select the text, then choose Add word.";

    /// <summary>The pop-up card right away; what is missing (the permission, a word to show) shows in the phone's cards of the settings.</summary>
    public Action? ShowWordNow => () => _overlay.ShowNowCommand.Execute(null);

    public Control? CreatePlatformSettings() => new VocabAndroidSettings(_overlay);

    /// <summary>On screen, or back from the phone's settings ("Display over other apps", "Usage access"): what is allowed now.</summary>
    public void Opened() => _overlay.RefreshPermissions();

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PageBack = _view.HandleBack;
        ActivityHost.Resumed += OnResumed;
        ShellIntents.ItemOffered += OnItemOffered;
        OpenOfferedWord();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module.PageBack == _view.HandleBack) _module.PageBack = null;
        ActivityHost.Resumed -= OnResumed;
        ShellIntents.ItemOffered -= OnItemOffered;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_view.Shown);

    private void OnItemOffered(object? sender, EventArgs e) => Dispatcher.UIThread.Post(OpenOfferedWord);

    /// <summary>"Open in Helm" on the pop-up card: that word with its whole tree.</summary>
    private void OpenOfferedWord()
    {
        if (ShellIntents.TakeItem(VocabIds.ModuleId) is { } wordId) _view.OpenWord(wordId);
    }
}
