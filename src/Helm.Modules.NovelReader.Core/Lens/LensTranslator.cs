using Helm.Core.Settings;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Text;
using Helm.Shell.Services;

namespace Helm.Modules.NovelReader.Lens;

/// <summary>A paragraph of the lens's page in study form: the converted line and each word's Chinese, pinyin and Hán Việt.</summary>
public sealed record LensRow(ConvertedLine Line, IReadOnlyList<WordReading?> Readings, bool IsHeading);

/// <summary>
/// Converts the paragraphs Novel Lens read into study form with the reader's dictionaries and the names of the chosen
/// novel (<see cref="NovelReaderSettings.LensBookId"/>, else the one open in the reader), and saves the names and
/// meanings fixed on the lens. A novel read only in another app gets a novel with just its title
/// (<see cref="CreateBook"/>) to keep its names. Nothing of the page is kept.
/// </summary>
public sealed class LensTranslator(NovelStore store, DictionaryLibrary dictionaries, ISettingsStoreFactory settings, IDeviceInfo device)
{
    private readonly ISettingsStore<NovelReaderSettings> _settings = settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId);
    private Converter? _converter;

    public NovelReaderSettings Settings => _settings.Current;

    public void Save(Action<NovelReaderSettings> change) => _settings.Update(change);

    /// <summary>Why the page cannot be converted yet (no dictionaries downloaded), or null.</summary>
    public string? Problem => dictionaries.HasFiles ? null : "No dictionaries yet: download them in Novel Reader first.";

    /// <summary>The novel whose names are used, or null when there is none.</summary>
    public string? BookId
    {
        get
        {
            var s = _settings.Current;
            var id = s.LensBookId ?? s.OpenBookId;
            return id is not null && store.GetBook(id) is not null ? id : null;
        }
    }

    /// <summary>The title of the novel whose names are used, or null.</summary>
    public string? BookTitle => BookId is { } id ? store.GetBook(id)?.Title : null;

    /// <summary>The novels to take names from, by title.</summary>
    public IReadOnlyList<(string Id, string Title)> Books() =>
        store.Books().Select(b => (b.Id, b.Value.Title)).OrderBy(b => b.Title, StringComparer.CurrentCulture).ToList();

    /// <summary>A novel with just this title (no text), chosen for the lens: the names fixed on the lens go to it.</summary>
    public string CreateBook(string title)
    {
        var id = store.AddNamesOnlyBook(title, device.DeviceName);
        _settings.Update(s => s.LensBookId = id);
        return id;
    }

    /// <summary>Saves a name or a meaning for the lens's novel (<paramref name="forNovel"/>) or for every novel.</summary>
    public void SaveEntry(string chinese, string vietnamese, EntryKind kind, bool forNovel)
    {
        chinese = chinese.Trim();
        vietnamese = vietnamese.Trim();
        if (chinese.Length == 0 || vietnamese.Length == 0) return;
        store.SaveEntry(forNovel ? BookId : null, kind, chinese, vietnamese);
    }

    /// <summary>Whether a name or a meaning was saved for this Chinese, for the lens's novel or for every novel.</summary>
    public bool HasEntry(string chinese)
    {
        chinese = chinese.Trim();
        if (chinese.Length == 0) return false;
        return new[] { BookId, null }.Distinct().Any(book =>
            store.GetEntry(book, EntryKind.Name, chinese) is not null || store.GetEntry(book, EntryKind.Phrase, chinese) is not null);
    }

    /// <summary>Removes what was saved for this Chinese: the lens's novel's entry, else the one for every novel.</summary>
    public void RemoveEntry(string chinese)
    {
        chinese = chinese.Trim();
        if (chinese.Length == 0) return;
        if (BookId is { } id && store.RemoveEntry(id, chinese)) return;
        store.RemoveEntry(null, chinese);
    }

    /// <summary>What to offer for a word fixed as a name: its Hán Việt in title case ("林清雪" → "Lâm Thanh Tuyết").</summary>
    public string SuggestName(string chinese) =>
        _converter is { } converter ? ChineseText.TitleCase(converter.HanVietOf(chinese)) : "";

    public async Task<IReadOnlyList<LensRow>> TranslateAsync(LensPage page, CancellationToken ct = default)
    {
        if (page.Paragraphs.Count == 0) return [];
        await EnsurePinyinAsync(ct).ConfigureAwait(false);
        var set = dictionaries.HasFiles ? await dictionaries.EnsureLoadedAsync().ConfigureAwait(false) : DictionarySet.Empty;
        var bookId = BookId;
        var prioritize = _settings.Current.PrioritizeNames;
        return await Task.Run(() =>
        {
            // Built for each page: names added in the reader or on the lens are used at once, and building is quick.
            var layers = bookId is null ? UserLayers.Empty : UserLayers.Build(store, bookId);
            var converter = new Converter(set, layers.BookNames, layers.UserNames, layers.Phrases, prioritize);
            _converter = converter;
            return (IReadOnlyList<LensRow>)page.Paragraphs.Select(p =>
            {
                var line = converter.ConvertLine(p.Chinese, ConvertMode.Study);
                return new LensRow(line, WordReading.Of(line, set.Pinyin, converter.HanVietOf), p.IsHeading);
            }).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Study form needs the pinyin lists: downloaded once (about 2 MB) like the reader's study mode does.</summary>
    private async Task EnsurePinyinAsync(CancellationToken ct)
    {
        if (!dictionaries.HasFiles || dictionaries.Files().Any(f => f.Kind == DictionaryKind.Pinyin)) return;
        await dictionaries.DownloadAsync(_settings.Current.PinyinSources(), null, ct).ConfigureAwait(false);
        await dictionaries.ReloadAsync().ConfigureAwait(false);
    }
}
