using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Dictionaries;

/// <summary>What a dictionary file holds, recognized from its name.</summary>
public enum DictionaryKind
{
    /// <summary>ChinesePhienAmWords.txt: one Hán Việt reading per character.</summary>
    HanViet,
    /// <summary>VietPhrase.txt (or several, e.g. VietPhrase_1.txt and VietPhrase_2.txt).</summary>
    VietPhrase,
    /// <summary>Names.txt: names of people, places and sects.</summary>
    Names,
    /// <summary>Pronouns.txt: what fills a LuatNhan slot besides names.</summary>
    Pronouns,
    /// <summary>LuatNhan.txt: reordering rules with a {0} slot.</summary>
    LuatNhan,
}

/// <summary>
/// The shared dictionaries a device downloaded or imported (not synced: VietPhrase alone is about 28 MB). Immutable once
/// loaded; the user's own names and phrases are separate layers (<see cref="UserLayers"/>).
/// </summary>
public sealed class DictionarySet
{
    public static DictionarySet Empty { get; } = new(new PhraseDictionary(), new PhraseDictionary(), new PhraseDictionary(), new PhraseDictionary(), new LuatNhanRules());

    public DictionarySet(PhraseDictionary hanViet, PhraseDictionary vietPhrase, PhraseDictionary names, PhraseDictionary pronouns, LuatNhanRules luatNhan)
    {
        HanViet = hanViet;
        VietPhrase = vietPhrase;
        Names = names;
        Pronouns = pronouns;
        LuatNhan = luatNhan;
    }

    public PhraseDictionary HanViet { get; }
    public PhraseDictionary VietPhrase { get; }
    public PhraseDictionary Names { get; }
    public PhraseDictionary Pronouns { get; }
    public LuatNhanRules LuatNhan { get; }

    /// <summary>Without Hán Việt readings nothing can be converted.</summary>
    public bool IsUsable => HanViet.Count > 0;

    public int CountOf(DictionaryKind kind) => kind switch
    {
        DictionaryKind.HanViet => HanViet.Count,
        DictionaryKind.VietPhrase => VietPhrase.Count,
        DictionaryKind.Names => Names.Count,
        DictionaryKind.Pronouns => Pronouns.Count,
        DictionaryKind.LuatNhan => LuatNhan.Count,
        _ => 0,
    };

    /// <summary>The kind a file name says it is, or null for a file Novel Reader does not use.</summary>
    public static DictionaryKind? KindOf(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Contains("phienam") || name.Contains("hanviet") || name.Contains("hán việt")) return DictionaryKind.HanViet;
        if (name.Contains("luatnhan")) return DictionaryKind.LuatNhan;
        if (name.Contains("pronoun")) return DictionaryKind.Pronouns;
        if (name.Contains("vietphrase")) return DictionaryKind.VietPhrase;
        if (name.Contains("name")) return DictionaryKind.Names;
        return null;
    }

    /// <summary>
    /// Reads every dictionary file in <paramref name="folder"/>. Several files of one kind are merged in name order
    /// (the first file wins on a duplicate key). Takes a second or two for a full set; call it off the UI thread.
    /// </summary>
    public static DictionarySet Load(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return Empty;
        var files = Directory.GetFiles(folder, "*.txt")
            .Select(path => (Path: path, Kind: KindOf(Path.GetFileName(path))))
            .Where(f => f.Kind is not null)
            .OrderBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        PhraseDictionary Merge(DictionaryKind kind)
        {
            PhraseDictionary? merged = null;
            foreach (var file in files.Where(f => f.Kind == kind))
            {
                ct.ThrowIfCancellationRequested();
                var text = TextFiles.ReadAllText(file.Path);
                if (merged is null) merged = PhraseDictionary.Parse(text);
                else merged.Load(text);
            }
            return merged ?? new PhraseDictionary();
        }
        var luatNhan = new LuatNhanRules();
        foreach (var file in files.Where(f => f.Kind == DictionaryKind.LuatNhan))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (key, value) in PhraseDictionary.ParseLines(TextFiles.ReadAllText(file.Path))) luatNhan.Add(key, value);
        }
        return new DictionarySet(Merge(DictionaryKind.HanViet), Merge(DictionaryKind.VietPhrase), Merge(DictionaryKind.Names),
            Merge(DictionaryKind.Pronouns), luatNhan);
    }
}
