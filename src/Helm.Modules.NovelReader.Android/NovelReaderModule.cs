using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Modules.NovelReader.Playback;
using Helm.Modules.NovelReader.Speech.VieNeu;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader on Android. Reading aloud keeps going with Helm in the background and the screen off, chapter after
/// chapter (<see cref="NovelReaderPlaybackService"/>); the place reading stopped is saved when Helm goes to the
/// background. The drawer, Quick access and Home → Utilities open <see cref="NovelReaderContentPage"/> (its settings are a
/// sheet inside it). Back closes an open sheet, then goes from a novel to the library (unless it is being read aloud:
/// then Back leaves it reading).
/// </summary>
public sealed class NovelReaderModule : AndroidModuleBase, IModuleContent, IBackHandler
{
    private readonly NovelReaderViewModel _viewModel;
    private readonly IUiDispatcher _ui;
    private readonly VieNeuSpeechEngine _vieneu;

    public NovelReaderModule(NovelReaderViewModel viewModel, IUiDispatcher ui, AndroidAudioOutput output, AndroidTtsEngine voices,
        VieNeuSpeechEngine vieneu)
    {
        _viewModel = viewModel;
        _ui = ui;
        _vieneu = vieneu;
        // The cover on the media notification and the lock screen.
        output.Artwork = () => viewModel.OpenBookCard?.Cover;
        // The phone's voices load after Helm starts: pick the saved one once they are there.
        voices.VoicesChanged += (_, _) => ui.Post(() => viewModel.RefreshVoicesCommand.Execute(null));
    }

    public override string Id => NovelReaderIds.ModuleId;
    public override string DisplayName => NovelReaderIds.DisplayName;
    public override string Description => NovelReaderIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override Symbol Icon => Symbol.BookOpen;

    /// <summary>The same vector icon as on Windows (<see cref="NovelReaderIconShape"/>).</summary>
    public override IImage? IconImage => Ui.NovelReaderIcon.Image;

    public override Type PageType => typeof(NovelReaderContentPage);
    public Type ContentPageType => typeof(NovelReaderContentPage);

    /// <summary>The reader page's own back step while it is shown (closing a sheet, leaving a novel): the view's HandleBack.</summary>
    internal Func<bool>? PageBack { get; set; }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        ActivityHost.Paused -= OnAppPaused;
        ActivityHost.Paused += OnAppPaused;
        KeepLensOn();
        return Task.CompletedTask;
    }

    /// <summary>Also runs when Helm exits or updates: reading aloud stops, the place reading stopped is saved and the VieNeu voice lets its model go.</summary>
    public override async Task DisableAsync()
    {
        ActivityHost.Paused -= OnAppPaused;
        await _ui.InvokeAsync(_viewModel.Release).ConfigureAwait(false);
        _vieneu.Release();
    }

    public bool HandleBack() => PageBack?.Invoke() ?? false;

    /// <summary>
    /// Novel Lens ships hidden (<see cref="Lens.LensComponent"/>). Someone who had it switched on before that keeps it:
    /// Android still has it in the switched-on list, so it is listed again. Once only: later, that list may still name
    /// a lens the user hid in Helm, and it must stay hidden.
    /// </summary>
    private static void KeepLensOn()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            using var prefs = context.GetSharedPreferences("helm-novel-lens", global::Android.Content.FileCreationMode.Private)!;
            if (prefs.GetBoolean("hidden-by-default", false)) return;
            if (Lens.LensComponent.IsSwitchedOn(context)) Lens.LensComponent.SetShown(context, true);
            using var edit = prefs.Edit()!;
            edit.PutBoolean("hidden-by-default", true)!.Apply();
        }
        catch (Exception)
        {
            // Only the lens is at stake; Helm starts anyway.
        }
    }

    /// <summary>Helm went to the background (another app, the screen off): save where reading is now.</summary>
    private void OnAppPaused(object? sender, EventArgs e) => _viewModel.FlushProgress();
}
