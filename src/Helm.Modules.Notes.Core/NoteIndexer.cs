using Helm.Core.Sync;
using Helm.Embeddings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Notes;

/// <summary>
/// Keeps <see cref="NoteVectors"/> in step with the notes on a device that has the sentence model: a while after notes
/// change (here or from another device), each note whose text changed since its vectors were made gets new ones, one
/// note at a time in the background; vectors of notes deleted for good go. A device without the model does nothing; the
/// vectors it gets from the others are enough to search. Runs from <see cref="Start"/> (the module is on) to
/// <see cref="Stop"/>; callable from any thread.
/// </summary>
public sealed class NoteIndexer : IDisposable
{
    /// <summary>Notes change in bursts (typing autosaves, a sync brings many): indexing waits for a quiet moment.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(15);

    private readonly NotesStore _store;
    private readonly ISyncedCollection<NoteVectors> _vectors;
    private readonly ITextEmbedder _embedder;
    private readonly ILogger<NoteIndexer> _logger;
    private readonly TimeSpan _quiet;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stopped = new();
    private volatile bool _started;
    private volatile bool _paused;

    public NoteIndexer(NotesStore store, ISyncedCollection<NoteVectors> vectors, ITextEmbedder embedder, ILogger<NoteIndexer> logger, TimeSpan? quiet = null)
    {
        _store = store;
        _vectors = vectors;
        _embedder = embedder;
        _logger = logger;
        _quiet = quiet ?? Quiet;
        _timer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        store.Changed += (_, _) => Schedule();
        embedder.AvailabilityChanged += (_, _) =>
        {
            Schedule();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Notes with vectors of the model in use for their current text, of <see cref="Total"/>.</summary>
    public int Indexed { get; private set; }

    /// <summary>Notes (the trash too: it can be searched).</summary>
    public int Total { get; private set; }

    /// <summary>A pass is running.</summary>
    public bool IsIndexing { get; private set; }

    /// <summary>The counts changed or a pass started or ended; may come on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Indexing goes on from here (the module was turned on); a first pass follows after a quiet moment.</summary>
    public void Start()
    {
        _started = true;
        Schedule();
    }

    /// <summary>No more passes (the module was turned off); a running one stops after its note.</summary>
    public void Stop()
    {
        _started = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Held while the phone's app is in the background (the model is large to load there); goes on after.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) Schedule();
        }
    }

    /// <summary>One pass now (tests, and the timer): returns when every note is indexed, or when it had to stop.</summary>
    public async Task RefreshAsync()
    {
        if (!_started || _paused || !_embedder.IsAvailable) return;
        if (!await _running.WaitAsync(0).ConfigureAwait(false))
        {
            // A pass is running: another follows it, for what changed meanwhile.
            Schedule();
            return;
        }
        try
        {
            await IndexAsync(_stopped.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A broken model or a note it cannot read: the next change tries again.
            _logger.LogWarning(ex, "Notes indexing stopped");
        }
        finally
        {
            IsIndexing = false;
            _running.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task IndexAsync(CancellationToken ct)
    {
        var notes = _store.Notes().Concat(_store.Trash()).ToList();
        var model = _embedder.ModelId;
        var stale = notes.Where(n => _vectors.Get(n.Id) is not { } v || v.Model != model || v.Hash != NoteVectors.HashOf(n.Value)).ToList();
        Total = notes.Count;
        Indexed = Total - stale.Count;
        IsIndexing = stale.Count > 0;
        Changed?.Invoke(this, EventArgs.Empty);

        var started = Environment.TickCount64;
        var done = 0;
        foreach (var note in stale)
        {
            if (!_started || _paused || !_embedder.IsAvailable) return;
            // It may have changed since the list was made (or been indexed by another device): index what it is now.
            if (_store.Get(note.Id) is not { } current) continue;
            var hash = NoteVectors.HashOf(current);
            if (_vectors.Get(note.Id) is { } existing && existing.Model == model && existing.Hash == hash) continue;
            var parts = await _embedder.EmbedPartsAsync(NoteVectors.TextOf(current), ct).ConfigureAwait(false);
            if (parts.Count == 0 && NoteVectors.TextOf(current).Trim().Length > 0) return;
            _vectors.Upsert(note.Id, new NoteVectors { Model = model, Hash = hash, Parts = [.. parts.Select(p => EmbeddingVector.Encode(p))] });
            done++;
            Indexed++;
            if (done % 10 == 0) Changed?.Invoke(this, EventArgs.Empty);
        }

        // Notes deleted for good take their vectors with them.
        var live = notes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _vectors.All().Where(v => !live.Contains(v.Id) && _store.Get(v.Id) is null).ToList()) _vectors.Delete(gone.Id);
        if (done > 0) _logger.LogInformation("Indexed {Count} notes in {Ms} ms", done, Environment.TickCount64 - started);
    }

    private void Schedule()
    {
        if (_started && !_paused && _embedder.IsAvailable) _timer.Change(_quiet, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        Stop();
        _stopped.Cancel();
        _timer.Dispose();
        _stopped.Dispose();
    }
}
