using Avalonia;
using Avalonia.Platform.Storage;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.NovelReader;

/// <summary>
/// What the shared <see cref="NovelReaderView"/> needs from Windows: the file pickers, making a cover small
/// (<see cref="CoverPicture"/>) and scraping the web (<see cref="NovelScraperViewModel"/>).
/// </summary>
internal sealed class WindowsNovelReaderPageHost(WordClassDictionary wordClasses, NovelScraperViewModel scraper) : INovelReaderPageHost
{
    private static readonly FilePickerFileType TextFiles = new("Text files") { Patterns = ["*.txt"] };

    /// <summary>The view this host serves (set right after the view is made): the pickers' owner.</summary>
    public Visual? View { get; set; }

    public INovelWebScraper? Scraper => scraper;

    public WordClassDictionary? WordClasses => wordClasses;

    public bool CanUseTextFiles => true;

    public bool CanOpenFolders => true;

    public bool CanAddSystemVoices => true;

    public async Task<string?> PickNovelAsync()
    {
        var files = await DesktopWindows.PickFilesAsync("Add a novel", [TextFiles, FilePickerFileTypes.All], anchor: View);
        return files.FirstOrDefault();
    }

    public async Task<byte[]?> PickCoverAsync(string bookTitle)
    {
        var pictures = new FilePickerFileType("Pictures") { Patterns = CoverPicture.Extensions.Select(x => "*" + x).ToList() };
        var files = await DesktopWindows.PickFilesAsync("Cover picture for " + bookTitle, [pictures], anchor: View);
        return files.Count > 0 ? Shrink(files[0]) : null;
    }

    public Task<byte[]?> CoverFromFileAsync(string path) => Task.FromResult(CoverPicture.IsPicture(path) ? Shrink(path) : null);

    public Task<IReadOnlyList<string>> PickDictionariesAsync() =>
        DesktopWindows.PickFilesAsync("Import dictionary files", [new FilePickerFileType("QuickTranslator dictionaries") { Patterns = ["*.txt"] }],
            many: true, anchor: View);

    public async Task<string?> PickTextFileAsync(string title)
    {
        var files = await DesktopWindows.PickFilesAsync(title, [new FilePickerFileType("Text") { Patterns = ["*.txt"] }], anchor: View);
        return files.FirstOrDefault();
    }

    public Task<string?> PickSaveFileAsync(string title, string fileName, string extension)
    {
        var label = extension.TrimStart('.') switch { "md" => "Markdown", "txt" => "Text", var other => other.ToUpperInvariant() };
        return DesktopWindows.PickSaveAsync(title, fileName, [new FilePickerFileType(label) { Patterns = ["*" + extension] }], View);
    }

    /// <summary>
    /// A small JPEG of the picture. A picture that cannot be read is an <see cref="InvalidOperationException"/>: the page
    /// shows its message ("Could not set the cover: …").
    /// </summary>
    private static byte[] Shrink(string path)
    {
        try
        {
            return CoverPicture.Shrink(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            throw new InvalidOperationException("That picture could not be read (" + ex.Message + ").", ex);
        }
    }
}
