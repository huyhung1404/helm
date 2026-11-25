using System.IO;
using System.Windows;
using System.Windows.Controls;
using Helm.Modules.Scratch.Ui;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// Avalonia <see cref="ScratchView"/> inside this WPF page, plus what only Windows does: the clipboard (files copied in
/// Explorer, a screenshot, text) and the files dropped on the page.
/// </summary>
public partial class ScratchContentPage : Page, IScratchPageHost
{
    private readonly ScratchViewModel _viewModel;
    private readonly ILogger<ScratchContentPage> _logger;

    public ScratchContentPage(ScratchModule module, ScratchViewModel viewModel, ILogger<ScratchContentPage> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        InitializeComponent();
        Host.Show(() => new ScratchView(module, viewModel, this));
    }

    public string AddHint => "Press Ctrl+V to add the files, picture or text you copied, or drop files on this page. On your phone, share photos to “Helm Scratch”.";

    /// <summary>Files copied in Explorer, then a picture (a screenshot), then text.</summary>
    public async Task PasteAsync()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                await AddPathsAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
                return;
            }
            if (WindowsScratchPlatform.FromClipboardImage(DateTimeOffset.Now) is { } picture)
            {
                await _viewModel.AddSourcesAsync([picture]);
                return;
            }
            if (Clipboard.ContainsText())
            {
                await _viewModel.AddTextAsync(Clipboard.GetText());
                return;
            }
            _viewModel.Message = "The clipboard has nothing to add. Copy files, a picture or text first.";
        }
        catch (Exception ex)
        {
            // The clipboard is shared with every app: it can be busy or hold something broken.
            _logger.LogWarning(ex, "Pasting into Scratch failed");
            _viewModel.Message = $"Could not paste: {ex.Message}";
        }
    }

    /// <summary>Files go in; folders are named in a message (their files can be added themselves).</summary>
    public async Task<bool> AddPathsAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (files.Count > 0) await _viewModel.AddSourcesAsync(files.Select(WindowsScratchPlatform.FromPath).ToList());
        if (folders.Count > 0)
        {
            var note = folders.Count == 1 ? $"“{Path.GetFileName(folders[0])}” is a folder" : $"{folders.Count} folders were skipped";
            _viewModel.Message = $"{(files.Count > 0 ? _viewModel.Message + " " : "")}{note}: add the files inside it, or a .zip of it.".Trim();
        }
        return files.Count > 0 || folders.Count > 0;
    }
}
