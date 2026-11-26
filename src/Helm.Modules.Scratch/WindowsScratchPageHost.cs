using Helm.Core.Desktop;
using Helm.Modules.Scratch.Ui;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Scratch;

/// <summary>
/// What only Windows does around the shared <see cref="ScratchView"/>: the clipboard (files copied in Explorer, a
/// screenshot, text) and the files dropped on the page.
/// </summary>
internal sealed class WindowsScratchPageHost(ScratchViewModel viewModel, ILogger logger) : IScratchPageHost
{
    public string AddHint => "Press Ctrl+V to add the files, picture or text you copied, or drop files on this page. On your phone, share photos to “Helm Scratch”.";

    /// <summary>Files copied in Explorer, then a picture (a screenshot), then text.</summary>
    public async Task PasteAsync()
    {
        try
        {
            if (WindowsClipboard.ContainsFiles())
            {
                await AddPathsAsync(WindowsClipboard.GetFiles());
                return;
            }
            if (await WindowsScratchPlatform.FromClipboardImageAsync(DateTimeOffset.Now) is { } picture)
            {
                await viewModel.AddSourcesAsync([picture]);
                return;
            }
            if (WindowsClipboard.ContainsText() && WindowsClipboard.GetText() is { } text)
            {
                await viewModel.AddTextAsync(text);
                return;
            }
            viewModel.Message = "The clipboard has nothing to add. Copy files, a picture or text first.";
        }
        catch (Exception ex)
        {
            // The clipboard is shared with every app: it can be busy or hold something broken.
            logger.LogWarning(ex, "Pasting into Scratch failed");
            viewModel.Message = $"Could not paste: {ex.Message}";
        }
    }

    /// <summary>Files go in; folders are named in a message (their files can be added themselves).</summary>
    public async Task<bool> AddPathsAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (files.Count > 0) await viewModel.AddSourcesAsync(files.Select(WindowsScratchPlatform.FromPath).ToList());
        if (folders.Count > 0)
        {
            var note = folders.Count == 1 ? $"“{Path.GetFileName(folders[0])}” is a folder" : $"{folders.Count} folders were skipped";
            viewModel.Message = $"{(files.Count > 0 ? viewModel.Message + " " : "")}{note}: add the files inside it, or a .zip of it.".Trim();
        }
        return files.Count > 0 || folders.Count > 0;
    }
}
