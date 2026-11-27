using Avalonia.Controls;
using Helm.Core;
using Helm.Core.Ui;
using Helm.Modules.Scratch.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// <see cref="ScratchView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>), plus what only
/// Android does: reading the phone's clipboard (content URIs another app copied). Back closes the details or the text box first
/// (see <see cref="ScratchModule"/>).
/// </summary>
public sealed class ScratchContentPage : UserControl, IFullPage, IScratchPageHost
{
    private readonly ScratchViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public ScratchContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<ScratchModule>(), HelmAndroidServices.Current.GetRequiredService<ScratchViewModel>())
    {
    }

    public ScratchContentPage(ScratchModule module, ScratchViewModel viewModel)
    {
        _viewModel = viewModel;
        Content = new ScratchView(module, viewModel, this);
    }

    public string AddHint => "Tip: in Gallery or any app, tap Share → Helm Scratch to put photos, videos and files here.";


    /// <summary>The phone's clipboard: files or pictures another app copied (content URIs), else its text.</summary>
    public async Task PasteAsync()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            if (context.GetSystemService(global::Android.Content.Context.ClipboardService) is not global::Android.Content.ClipboardManager clipboard
                || clipboard.PrimaryClip is not { ItemCount: > 0 } clip)
            {
                _viewModel.Message = "The clipboard is empty. Copy a text, a link or a picture first.";
                return;
            }
            var sources = new List<ScratchSource>();
            var texts = new List<string>();
            for (var i = 0; i < clip.ItemCount; i++)
            {
                var item = clip.GetItemAt(i);
                if (item?.Uri is { Scheme: "content" } uri) sources.Add(AndroidScratchPlatform.FromUri(uri));
                else if (item?.CoerceToText(context)?.ToString() is { Length: > 0 } text) texts.Add(text);
            }
            if (sources.Count > 0) await _viewModel.AddSourcesAsync(sources);
            else if (texts.Count > 0) await _viewModel.AddTextAsync(string.Join('\n', texts));
            else _viewModel.Message = "The clipboard has nothing Scratch can keep.";
        }
        catch (Exception ex)
        {
            // Another app's content provider can refuse or vanish.
            _viewModel.Message = $"Could not paste: {ex.Message}";
        }
    }
}
