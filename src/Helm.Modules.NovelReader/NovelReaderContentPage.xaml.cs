using System.Windows;
using System.Windows.Controls;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Ui;
using Helm.Shell.Services;
using Microsoft.Win32;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the
/// shared Avalonia <see cref="NovelReaderView"/> inside this WPF page, plus what only Windows does for it: the file
/// dialogs, making a cover small (WPF imaging) and scraping the web (<see cref="NovelScraperViewModel"/>).
/// </summary>
public partial class NovelReaderContentPage : Page, INovelReaderPageHost
{
    private readonly NovelScraperViewModel _scraper;
    private readonly WordClassDictionary _wordClasses;

    public NovelReaderContentPage(NovelReaderModule module, NovelReaderViewModel viewModel, IClipboardService clipboard,
        WordClassDictionary wordClasses, NovelScraperViewModel scraper)
    {
        _wordClasses = wordClasses;
        _scraper = scraper;
        InitializeComponent();
        Host.Show(() => new NovelReaderView(module, viewModel, this, clipboard));
    }

    public INovelWebScraper? Scraper => _scraper;

    public WordClassDictionary? WordClasses => _wordClasses;

    public bool CanUseTextFiles => true;

    public bool CanOpenFolders => true;

    public bool CanAddSystemVoices => true;

    public Task<string?> PickNovelAsync()
    {
        var dialog = new OpenFileDialog { Title = "Add a novel", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        return Task.FromResult(dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null);
    }

    public Task<byte[]?> PickCoverAsync(string bookTitle)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Cover picture for " + bookTitle,
            Filter = "Pictures|" + string.Join(";", CoverPicture.Extensions.Select(x => "*" + x)),
        };
        return Task.FromResult(dialog.ShowDialog(Window.GetWindow(this)) == true ? Shrink(dialog.FileName) : null);
    }

    public Task<byte[]?> CoverFromFileAsync(string path) => Task.FromResult(CoverPicture.IsPicture(path) ? Shrink(path) : null);

    public Task<IReadOnlyList<string>> PickDictionariesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import dictionary files",
            Filter = "QuickTranslator dictionaries (*.txt)|*.txt",
            Multiselect = true,
        };
        return Task.FromResult<IReadOnlyList<string>>(dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : []);
    }

    public Task<string?> PickTextFileAsync(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "Text (*.txt)|*.txt" };
        return Task.FromResult(dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null);
    }

    public Task<string?> PickSaveFileAsync(string title, string fileName, string extension)
    {
        var label = extension.TrimStart('.') switch { "md" => "Markdown", "txt" => "Text", var other => other.ToUpperInvariant() };
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = $"{label} (*{extension})|*{extension}" };
        return Task.FromResult(dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null);
    }

    /// <summary>
    /// A small JPEG of the picture (WPF imaging draws on the UI thread; one picture is quick). A picture Windows cannot read
    /// is an <see cref="InvalidOperationException"/>: the page shows its message ("Could not set the cover: …").
    /// </summary>
    private static byte[] Shrink(string path)
    {
        try
        {
            return CoverPicture.Shrink(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or ArgumentException or FileFormatException)
        {
            throw new InvalidOperationException("That picture could not be read (" + ex.Message + ").", ex);
        }
    }
}
