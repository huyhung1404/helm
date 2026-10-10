using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader.Dictionaries;

/// <summary>A dictionary file on this device.</summary>
public sealed record DictionaryFile(string Name, DictionaryKind Kind, long Size);

/// <summary>
/// The shared dictionaries, kept in settings/novel-reader/dictionaries and synced: each file is an encrypted blob
/// (collection <see cref="Collection"/>), so a new device or a reinstall fetches them by itself and nothing is lost if a
/// download source disappears. Files added on this device (imported, downloaded, or already there before syncing) are
/// published; files changed on another device replace the local ones. Loaded in the background the first time a novel
/// is opened and released when the tool is turned off. Thread-safe.
/// </summary>
public sealed class DictionaryLibrary
{
    public const string Collection = "novel.dictionaries";

    private const string ManifestName = ".synced.json";

    private readonly ISyncedCollection<NovelDictionary> _records;
    private readonly BlobStore _blobs;
    private readonly IDeviceInfo _device;
    private readonly ILogger<DictionaryLibrary> _logger;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _syncing = new(1, 1);
    private Task<DictionarySet>? _loading;
    private int _remoteChangeQueued;

    public DictionaryLibrary(ISettingsStoreFactory settings, ISyncedCollection<NovelDictionary> records, BlobStore blobs, IDeviceInfo device,
        ILogger<DictionaryLibrary> logger, HttpMessageHandler? handler = null)
    {
        _records = records;
        _blobs = blobs;
        _device = device;
        _logger = logger;
        Folder = Path.Combine(settings.Paths.ModuleDataDirectory(NovelReaderIds.ModuleId), "dictionaries");
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Helm-NovelReader");
        _records.Changed += (_, e) =>
        {
            if (e.Origin == SyncChangeOrigin.Remote) _ = OnRemoteChangeAsync();
        };
    }

    /// <summary>Last writer wins: a dictionary replaced on one device replaces it everywhere.</summary>
    public static SyncedCollectionOptions<NovelDictionary> Options { get; } = new()
    {
        Name = Collection,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
        BlobReferences = d => d.Blob is { } blob ? [blob.Id] : [],
    };

    public string Folder { get; }

    /// <summary>What is loaded now (<see cref="DictionarySet.Empty"/> before the first load).</summary>
    public DictionarySet Current { get; private set; } = DictionarySet.Empty;

    public bool IsLoaded { get; private set; }

    /// <summary>Raised after a load, an import, a download, a synced change or an unload, on a background thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<DictionaryFile> Files()
    {
        var files = new Dictionary<string, DictionaryFile>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Folder))
        {
            foreach (var path in Directory.GetFiles(Folder, "*.txt"))
            {
                var name = Path.GetFileName(path);
                if (DictionarySet.KindOf(name) is { } kind) files[name] = new DictionaryFile(name, kind, new FileInfo(path).Length);
            }
        }
        // Synced from another device and not downloaded yet.
        foreach (var record in _records.All().Select(r => r.Value))
            if (!files.ContainsKey(record.Name) && DictionarySet.KindOf(record.Name) is { } kind)
                files[record.Name] = new DictionaryFile(record.Name, kind, record.Size);
        return files.Values.OrderBy(f => f.Kind).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Hán Việt readings are here or synced: novels can be converted.</summary>
    public bool HasFiles => Files().Any(f => f.Kind == DictionaryKind.HanViet);

    /// <summary>Fetches synced dictionaries and loads them once; later calls wait for the same load.</summary>
    public Task<DictionarySet> EnsureLoadedAsync()
    {
        lock (_gate)
        {
            if (IsLoaded) return Task.FromResult(Current);
            return _loading ??= Task.Run(LoadNowAsync);
        }
    }

    /// <summary>Reads the files again (after an import, a download or a synced change).</summary>
    public Task<DictionarySet> ReloadAsync()
    {
        lock (_gate)
        {
            IsLoaded = false;
            _loading = Task.Run(LoadNowAsync);
            return _loading;
        }
    }

    /// <summary>Frees the memory (VietPhrase alone takes about 100 MB) while the tool is off.</summary>
    public void Unload()
    {
        lock (_gate)
        {
            if (!IsLoaded && _loading is null) return;
            Current = DictionarySet.Empty;
            IsLoaded = false;
            _loading = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Copies the dictionary files among <paramref name="paths"/> into the folder (replacing ones with the same name)
    /// and syncs them. Returns the names of the files that were not recognized.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        var skipped = new List<string>();
        var copied = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (DictionarySet.KindOf(name) is null)
            {
                skipped.Add(name);
                continue;
            }
            File.Copy(path, Path.Combine(Folder, name), overwrite: true);
            copied.Add(name);
        }
        foreach (var name in copied) await PublishAsync(name, ct).ConfigureAwait(false);
        return skipped;
    }

    /// <summary>
    /// Downloads each file of <paramref name="sources"/> (name → address) into the folder, through a ".part" file so a
    /// broken download never replaces a good file, and syncs it. Progress is 0 to 1 over all files.
    /// </summary>
    public async Task DownloadAsync(IReadOnlyDictionary<string, string> sources, IProgress<(string File, double Fraction)>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var files = sources.Where(s => DictionarySet.KindOf(s.Key) is not null && Uri.TryCreate(s.Value, UriKind.Absolute, out _)).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            var (name, url) = files[i];
            name = Path.GetFileName(name);
            var target = Path.Combine(Folder, name);
            var part = target + ".part";
            var index = i;
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var destination = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    var fraction = total is > 0 ? Math.Min(1, (double)done / total.Value) : 0.5;
                    progress?.Report((name, (index + fraction) / files.Count));
                }
            }
            File.Move(part, target, overwrite: true);
            _logger.LogInformation("Downloaded dictionary {File}", name);
            await PublishAsync(name, ct).ConfigureAwait(false);
        }
        progress?.Report(("", 1));
    }

    /// <summary>
    /// Brings the folder and the synced records together: files synced from another device are fetched, files only on
    /// this device are published. Returns true when a local file changed.
    /// </summary>
    public async Task<bool> SyncFilesAsync(CancellationToken ct = default)
    {
        await _syncing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Folder);
            var manifest = ReadManifest();
            var changed = false;
            var records = _records.All().ToDictionary(r => r.Value.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var record in records.Values)
            {
                if (record.Blob is not { } blob || DictionarySet.KindOf(record.Name) is null) continue;
                var path = Path.Combine(Folder, Path.GetFileName(record.Name));
                if (File.Exists(path) && manifest.TryGetValue(record.Name, out var have) && have == blob.Id) continue;
                try
                {
                    var part = path + ".part";
                    await using (var destination = File.Create(part))
                        await _blobs.ReadAsync(blob, destination, null, ct).ConfigureAwait(false);
                    File.Move(part, path, overwrite: true);
                    manifest[record.Name] = blob.Id;
                    changed = true;
                    _logger.LogInformation("Fetched synced dictionary {File}", record.Name);
                }
                catch (BlobUnavailableException ex)
                {
                    // Not uploaded yet, or sync is off: try again on the next change or load.
                    _logger.LogInformation("Synced dictionary {File} is not available yet: {Reason}", record.Name, ex.Message);
                }
            }
            WriteManifest(manifest);
            foreach (var path in Directory.GetFiles(Folder, "*.txt"))
            {
                var name = Path.GetFileName(path);
                if (DictionarySet.KindOf(name) is null || records.ContainsKey(name)) continue;
                await PublishLockedAsync(name, ct).ConfigureAwait(false);
            }
            return changed;
        }
        finally
        {
            _syncing.Release();
        }
    }

    private async Task PublishAsync(string name, CancellationToken ct)
    {
        await _syncing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PublishLockedAsync(name, ct).ConfigureAwait(false);
        }
        finally
        {
            _syncing.Release();
        }
    }

    /// <summary>Encrypts one local file into a blob and points its record at it (the old blob is dropped).</summary>
    private async Task PublishLockedAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(Folder, name);
        BlobRef blob;
        await using (var content = File.OpenRead(path))
            blob = await _blobs.ImportAsync(content, null, ct).ConfigureAwait(false);
        var id = name.ToLowerInvariant();
        var old = _records.Get(id);
        try
        {
            _records.Upsert(id, new NovelDictionary
            {
                Name = name,
                Size = blob.Size,
                Blob = blob,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedFrom = _device.DeviceName,
            });
        }
        catch
        {
            _blobs.Discard(blob.Id);
            throw;
        }
        if (old?.Blob is { } previous && previous.Id != blob.Id) _blobs.Discard(previous.Id);
        var manifest = ReadManifest();
        manifest[name] = blob.Id;
        WriteManifest(manifest);
        _logger.LogInformation("Synced dictionary {File} ({Size} bytes)", name, blob.Size);
    }

    private async Task OnRemoteChangeAsync()
    {
        // Several records arrive in one pull; handle them once.
        if (Interlocked.Exchange(ref _remoteChangeQueued, 1) == 1) return;
        try
        {
            await Task.Delay(500).ConfigureAwait(false);
            Interlocked.Exchange(ref _remoteChangeQueued, 0);
            if (await SyncFilesAsync().ConfigureAwait(false))
            {
                if (IsLoaded) await ReloadAsync().ConfigureAwait(false);
                else Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _remoteChangeQueued, 0);
            _logger.LogWarning(ex, "Could not fetch the synced dictionaries");
        }
    }

    private async Task<DictionarySet> LoadNowAsync()
    {
        try
        {
            try
            {
                await SyncFilesAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Loading what is on this device is still useful.
                _logger.LogWarning(ex, "Could not sync the dictionaries");
            }
            var started = Environment.TickCount64;
            var set = DictionarySet.Load(Folder);
            lock (_gate)
            {
                Current = set;
                IsLoaded = true;
            }
            _logger.LogInformation("Loaded dictionaries in {Ms} ms: {Phrases} phrases, {Names} names", Environment.TickCount64 - started,
                set.VietPhrase.Count, set.Names.Count);
            Changed?.Invoke(this, EventArgs.Empty);
            return set;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the dictionaries");
            lock (_gate) _loading = null;
            throw;
        }
    }

    /// <summary>Which synced blob each local file holds, so a file is fetched only when another device replaced it.</summary>
    private Dictionary<string, string> ReadManifest()
    {
        try
        {
            var path = Path.Combine(Folder, ManifestName);
            if (File.Exists(path))
                return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [],
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read the dictionary manifest");
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void WriteManifest(Dictionary<string, string> manifest) =>
        Text.TextFiles.WriteAllTextAtomic(Path.Combine(Folder, ManifestName), JsonSerializer.Serialize(manifest));
}
