using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Scraping;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>
/// What each platform does its own way around the shared Novel Reader page: picking and saving files, making a cover
/// small, scraping the web (the PC drives a browser), the phone's own voices and battery, and Novel Lens (Android). Every
/// member that a platform lacks has a default, and the page leaves out what it would offer.
/// </summary>
public interface INovelReaderPageHost
{
    /// <summary>The page came on screen, or the app came back to it (Android starts the phone's own voices).</summary>
    void Opened() { }

    /// <summary>Asks for a novel (.txt) and gives a path the page can read; null when cancelled.</summary>
    Task<string?> PickNovelAsync();

    /// <summary>Asks for a picture and makes a small JPEG cover of it (at most 600 px); null when cancelled.</summary>
    Task<byte[]?> PickCoverAsync(string bookTitle);

    /// <summary>A picture dropped on a novel's card, made into a small JPEG cover; null when it is not a picture.</summary>
    Task<byte[]?> CoverFromFileAsync(string path) => Task.FromResult<byte[]?>(null);

    /// <summary>Asks for QuickTranslator dictionary files and copies them where they can be read; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickDictionariesAsync();

    /// <summary>The page can open and save text files (names as Names.txt, the reports as Markdown).</summary>
    bool CanUseTextFiles => false;

    /// <summary>Asks for a text file to read; null when cancelled.</summary>
    Task<string?> PickTextFileAsync(string title) => Task.FromResult<string?>(null);

    /// <summary>Asks where to save a file (<paramref name="extension"/> like ".md"); null when cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string fileName, string extension) => Task.FromResult<string?>(null);

    /// <summary>The dictionary folder can be opened in the system's file manager (the PC).</summary>
    bool CanOpenFolders => false;

    /// <summary>The system has voices to add in its own settings (Windows: Settings → Speech), shown as "Add voices".</summary>
    bool CanAddSystemVoices => false;

    /// <summary>A mouse points at the text: the paragraph under it can be marked for reading by eye (not on a phone).</summary>
    bool HasPointer => true;

    /// <summary>Scraping a novel from the web (the PC, through its browser). Null: the phone asks the PC instead.</summary>
    INovelWebScraper? Scraper => null;

    /// <summary>The Chinese word classes for colouring subjects, verbs and objects; null colours no roles.</summary>
    WordClassDictionary? WordClasses => null;

    /// <summary>The phone's own voices and its battery settings (Android). Null: none.</summary>
    INovelPhone? Phone => null;

    /// <summary>Novel Lens over the Fanqie app (Android 11 and later). Null: not on this platform.</summary>
    INovelLens? Lens => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>
/// "Scrape from web" (the PC): a chapter-list address or a novel's name, the novels found, the download's progress, and
/// the novel's new chapters later ("Continue download"). Windows' NovelScraperViewModel implements it.
/// </summary>
public interface INovelWebScraper : INotifyPropertyChanged
{
    /// <summary>A chapter-list address, or a novel's name to look for.</summary>
    string Url { get; set; }

    string StatusText { get; }

    bool HasStatus { get; }

    /// <summary>Which browser it uses, and whether it is connected.</summary>
    string BrowserStatus { get; }

    bool IsRunning { get; }

    bool IsIndeterminate { get; }

    /// <summary>0 to 1.</summary>
    double Progress { get; }

    /// <summary>The novels found by name; <see cref="PickCommand"/> scrapes one.</summary>
    IEnumerable<SearchHit> Results { get; }

    bool HasResults { get; }

    /// <summary>The first chapters are in: the novel can be read while the rest come.</summary>
    bool CanReadNow { get; }

    /// <summary>Scrapes the address, or looks for the name.</summary>
    ICommand AutoCommand { get; }

    /// <summary>A <see cref="SearchHit"/>: scrapes it.</summary>
    ICommand PickCommand { get; }

    ICommand ReadNowCommand { get; }

    ICommand CancelCommand { get; }

    /// <summary>Downloads the chapters that failed again (the preview's "Retry failed").</summary>
    ICommand RetryFailedCommand { get; }

    /// <summary>A <see cref="BookCardViewModel"/>: its new chapters (and failed ones) from its web source.</summary>
    ICommand ContinueCommand { get; }

    /// <summary>Opens the site in a browser window to log in (a <see cref="BookCardViewModel"/>, or none for the address typed).</summary>
    ICommand LogInCommand { get; }
}

/// <summary>What only the phone has: its own text-to-speech voices and the battery settings that keep reading aloud going.</summary>
public interface INovelPhone
{
    /// <summary>Reads a sentence at once with one of the phone's voices, to choose one.</summary>
    void PreviewVoice(SpeechVoice voice, string text);

    /// <summary>The engine's own screen for downloading voice data (e.g. Vietnamese for Google's engine).</summary>
    void InstallVoiceData();

    /// <summary>Android's text-to-speech settings (preferred engine, its voices).</summary>
    void OpenVoiceSettings();

    /// <summary>The phone's battery optimization list, where Helm can be allowed to run in the background.</summary>
    void OpenBatterySettings();

    /// <summary>Android does not limit Helm in the background (battery optimization is off for it).</summary>
    bool RunsFreelyInBackground { get; }
}

/// <summary>
/// Novel Lens (Android): a 译 button over the Fanqie app that reads its page in study form. The settings sheet shows its
/// state, the novel its names come from and where Next taps; <see cref="Refresh"/> reads them again.
/// </summary>
public interface INovelLens
{
    /// <summary>Hidden, almost on, on: one sentence.</summary>
    string State { get; }

    /// <summary>False on Android before 11: the lens cannot run.</summary>
    bool IsSupported { get; }

    /// <summary>Listed as an accessibility service (it can be switched on, and must be hidden again before a banking app).</summary>
    bool IsShown { get; }

    /// <summary>"The novel open in Novel Reader" first, then the novels by title.</summary>
    IReadOnlyList<string> Books { get; }

    int BookIndex { get; set; }

    /// <summary>Where Next taps Fanqie to turn the page, as a part of the width (0.6 to 0.98).</summary>
    double NextTap { get; set; }

    /// <summary>Lists the lens again and opens Accessibility settings to switch it on.</summary>
    void TurnOn();

    /// <summary>Hides the lens: Android stops it and it leaves Accessibility settings.</summary>
    void TurnOff();

    /// <summary>A novel with only a title, for the names of a novel read only in Fanqie; it becomes the lens's novel.</summary>
    void CreateBook(string title);

    void Refresh();
}
