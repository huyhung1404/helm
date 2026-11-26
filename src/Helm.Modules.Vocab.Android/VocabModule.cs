using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary on Android. While it is on, a word to guess pops up over the other apps now and then, and an optional
/// floating bubble shows one when tapped (<see cref="VocabOverlay"/>); both stay out of blocked apps and away while the
/// phone is sideways. No notification and no widget.
/// </summary>
public sealed class VocabModule : AndroidModuleBase, IModuleContent, IBackHandler
{
    private readonly VocabOverlay _overlay;

    public VocabModule(VocabOverlay overlay)
    {
        _overlay = overlay;
    }

    public override string Id => VocabIds.ModuleId;
    public override string DisplayName => VocabIds.DisplayName;
    public override string Description => VocabIds.Description;
    public override ModuleGroup Group => ModuleGroup.Learning;
    public override Symbol Icon => Symbol.BookLetter;

    /// <summary>The same vector icon as on Windows (<see cref="VocabIconShape"/>, drawn in Helm.Modules.Vocab.UI).</summary>
    public override IImage? IconImage => Ui.VocabIcon.Image;

    public override Type PageType => typeof(VocabContentPage);
    public Type ContentPageType => typeof(VocabContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        _overlay.Start();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        _overlay.Stop();
        return Task.CompletedTask;
    }

    /// <summary>The shown Vocabulary page's own back (its sheets, a word's page); set while the page is on screen.</summary>
    internal Func<bool>? PageBack { get; set; }

    public bool HandleBack() => PageBack?.Invoke() == true;
}
