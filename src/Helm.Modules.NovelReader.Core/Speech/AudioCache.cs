using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Helm.Modules.NovelReader.Speech;

/// <summary>
/// Synthesized sentences kept on this device (cache/novel-reader/audio/&lt;book&gt;/&lt;hash&gt;.mp3), so a sentence
/// downloaded once plays at once and offline, also after Helm restarts. A sentence is the same sound only for the
/// same voice, speed, pitch, volume and text, so changing one of them downloads again (the old files stay until
/// cleared). Not synced: a long novel is hundreds of megabytes. Thread-safe.
/// </summary>
public sealed class AudioCache
{
    private const string ManifestName = "chapters.json";

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);

    public AudioCache(string root) => Root = root;

    public string Root { get; }

    /// <summary>What makes two recordings of a text the same: the voice and its speed, pitch and volume.</summary>
    public static string Profile(SpeechVoice voice, SpeechOptions options) =>
        string.Create(CultureInfo.InvariantCulture, $"{voice.Id}|{options.Rate:0.##}|{options.Pitch:0.##}|{options.Volume:0.##}");

    /// <summary>The file name of a sentence: a hash, so neither the voice nor the text shows in the folder.</summary>
    public static string Key(string profile, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile + "|" + text)), 0, 16).ToLowerInvariant();

    public bool Contains(string book, string key) => File.Exists(PathOf(book, key, "mp3")) || File.Exists(PathOf(book, key, "wav"));

    public SpeechAudio? TryGet(string book, string key)
    {
        foreach (var (extension, type) in new[] { ("mp3", "audio/mpeg"), ("wav", "audio/wav") })
        {
            var path = PathOf(book, key, extension);
            try
            {
                if (File.Exists(path)) return new SpeechAudio(File.ReadAllBytes(path), type);
            }
            catch (IOException)
            {
                // Being written or removed right now: treat it as missing.
            }
        }
        return null;
    }

    /// <summary>Keeps a sentence, through a ".part" file so a crash never leaves half a sound.</summary>
    public void Put(string book, string key, SpeechAudio audio)
    {
        var extension = audio.ContentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? "mp3" : "wav";
        var path = PathOf(book, key, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        File.WriteAllBytes(part, audio.Data);
        File.Move(part, path, overwrite: true);
        lock (_gate)
            if (_sizes.TryGetValue(book, out var size)) _sizes[book] = size + audio.Data.Length;
    }

    /// <summary>Bytes kept for one novel (or all of them when <paramref name="book"/> is null).</summary>
    public long SizeOf(string? book)
    {
        if (book is null)
            return Directory.Exists(Root) ? Directory.GetDirectories(Root).Sum(d => SizeOf(Path.GetFileName(d))) : 0;
        lock (_gate)
            if (_sizes.TryGetValue(book, out var known)) return known;
        var folder = Path.Combine(Root, book);
        long total = 0;
        if (Directory.Exists(folder))
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
                if (file.Extension is ".mp3" or ".wav") total += file.Length;
        }
        lock (_gate) _sizes[book] = total;
        return total;
    }

    /// <summary>Deletes the kept sound of one novel, or of all novels.</summary>
    public void Clear(string? book)
    {
        lock (_gate)
        {
            if (book is null) _sizes.Clear();
            else _sizes.Remove(book);
        }
        var folder = book is null ? Root : Path.Combine(Root, book);
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A file is playing; what is left goes the next time.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- Downloaded chapters (a hint for the chapter list) ----------------------------------------------------------

    /// <summary>Remembers that every sentence of a chapter is kept for <paramref name="profile"/>.</summary>
    public void MarkChapter(string book, int chapter, string profile)
    {
        lock (_gate)
        {
            var manifest = ReadManifest(book);
            if (!manifest.TryGetValue(profile, out var chapters)) manifest[profile] = chapters = [];
            if (chapters.Contains(chapter)) return;
            chapters.Add(chapter);
            WriteManifest(book, manifest);
        }
    }

    /// <summary>The chapters downloaded in full for <paramref name="profile"/>.</summary>
    public IReadOnlySet<int> DownloadedChapters(string book, string profile)
    {
        lock (_gate)
            return ReadManifest(book).TryGetValue(profile, out var chapters) ? chapters.ToHashSet() : new HashSet<int>();
    }

    private Dictionary<string, List<int>> ReadManifest(string book)
    {
        var path = Path.Combine(Root, book, ManifestName);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, List<int>>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return [];
    }

    private void WriteManifest(string book, Dictionary<string, List<int>> manifest)
    {
        var path = Path.Combine(Root, book, ManifestName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Text.TextFiles.WriteAllTextAtomic(path, JsonSerializer.Serialize(manifest));
    }

    private string PathOf(string book, string key, string extension) => Path.Combine(Root, book, key + "." + extension);
}
