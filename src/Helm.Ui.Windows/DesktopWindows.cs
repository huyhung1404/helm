using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Helm.Ui.Windows;

/// <summary>
/// The app's windows, for code that has no control of its own at hand (a module's host, a service): the window in front,
/// and its file pickers. With a control, prefer <c>TopLevel.GetTopLevel(control)</c>.
/// </summary>
public static class DesktopWindows
{
    /// <summary>The active window, else the main window; null before the app has one.</summary>
    public static Window? Active =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;

    /// <summary>The storage provider (open/save pickers) of <paramref name="anchor"/>'s window, else of the active one.</summary>
    public static IStorageProvider? Storage(Visual? anchor = null) =>
        (anchor is not null ? TopLevel.GetTopLevel(anchor) : null)?.StorageProvider ?? Active?.StorageProvider;

    /// <summary>Asks for files to open; the local paths chosen (none when cancelled).</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FilePickerFileType>? types = null, bool many = false, Visual? anchor = null)
    {
        if (Storage(anchor) is not { } storage) return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = many, FileTypeFilter = types }).ConfigureAwait(true);
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>Asks where to save; the local path chosen, or null when cancelled.</summary>
    public static async Task<string?> PickSaveAsync(string title, string suggestedName, IReadOnlyList<FilePickerFileType>? types = null, Visual? anchor = null)
    {
        if (Storage(anchor) is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, FileTypeChoices = types })
            .ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    /// <summary>Asks for a folder; its local path, or null when cancelled.</summary>
    public static async Task<string?> PickFolderAsync(string title, Visual? anchor = null)
    {
        if (Storage(anchor) is not { } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title }).ConfigureAwait(true);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
