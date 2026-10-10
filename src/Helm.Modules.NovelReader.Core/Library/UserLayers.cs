using Helm.Modules.NovelReader.Dictionaries;

namespace Helm.Modules.NovelReader.Library;

/// <summary>The user's saved entries as converter layers: names for this novel, names for all, and meanings.</summary>
public sealed record UserLayers(PhraseDictionary BookNames, PhraseDictionary UserNames, PhraseDictionary Phrases)
{
    public static UserLayers Empty { get; } = new(new PhraseDictionary(), new PhraseDictionary(), new PhraseDictionary());

    /// <summary>A meaning saved for this novel wins over one saved for all novels.</summary>
    public static UserLayers Build(NovelStore store, string? bookId)
    {
        var bookNames = new PhraseDictionary();
        var userNames = new PhraseDictionary();
        var phrases = new PhraseDictionary();
        if (bookId is not null)
        {
            foreach (var entry in store.Entries(bookId))
            {
                if (entry.Kind == EntryKind.Name) bookNames.Set(entry.Chinese, entry.Vietnamese);
                else phrases.Set(entry.Chinese, entry.Vietnamese);
            }
        }
        foreach (var entry in store.Entries(null))
        {
            if (entry.Kind == EntryKind.Name) userNames.Set(entry.Chinese, entry.Vietnamese);
            else phrases.TryAdd(entry.Chinese, entry.Vietnamese);
        }
        return new UserLayers(bookNames, userNames, phrases);
    }

    /// <summary>The entries of one kind as a QuickTranslator file (Names.txt or VietPhrase.txt), to keep or share.</summary>
    public static string Export(IEnumerable<NovelEntry> entries, EntryKind kind) =>
        PhraseDictionary.Format(entries.Where(e => e.Kind == kind).Select(e => new KeyValuePair<string, string>(e.Chinese, e.Vietnamese)));
}
