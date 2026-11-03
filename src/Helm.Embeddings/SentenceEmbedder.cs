using Helm.Core.Downloads;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Helm.Embeddings;

/// <summary>
/// <see cref="ITextEmbedder"/> on the sentence model, once it is downloaded (<see cref="SentenceModelInstaller"/>). The
/// model is loaded with the first text (about a second) and let go after <see cref="IdleRelease"/> without one, so it
/// takes no memory while nothing is searched or indexed. One text at a time; callable from any thread.
/// </summary>
public sealed class SentenceEmbedder : ITextEmbedder, IDisposable
{
    public static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(5);

    /// <summary>Parts of a long text that get a vector; the rest of a very long note is found by its words only.</summary>
    public const int MaxParts = 32;

    private static readonly int Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private readonly SentenceModelInstaller _installer;
    private readonly ILogger<SentenceEmbedder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private (UnigramTokenizer Tokenizer, SentenceModel Model)? _loaded;
    private bool _broken;

    public SentenceEmbedder(SentenceModelInstaller installer, ILogger<SentenceEmbedder> logger)
    {
        _installer = installer;
        _logger = logger;
        _idle = new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
        IsAvailable = installer.Files.IsInstalled;
        installer.InstalledChanged += (_, _) =>
        {
            Release();
            _broken = false;
            IsAvailable = installer.State == PinnedDownloadState.Installed;
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public string ModelId => SentenceModelFiles.ModelId;

    public bool IsAvailable { get; private set; }

    public event EventHandler? AvailabilityChanged;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vectors = await RunAsync(t => [t.Encode(text, SentenceModel.MaxTokens)], ct).ConfigureAwait(false);
        return vectors.Count == 1 ? vectors[0] : null;
    }

    public Task<IReadOnlyList<float[]>> EmbedPartsAsync(string text, CancellationToken ct = default) =>
        RunAsync(t => Parts(t, text), ct);

    /// <summary>
    /// The text cut in parts of whole words that fit the model, each between &lt;s&gt; and &lt;/s&gt; (a word too long
    /// for one part is cut too), at most <see cref="MaxParts"/>.
    /// </summary>
    internal static List<IReadOnlyList<int>> Parts(UnigramTokenizer tokenizer, string text)
    {
        const int room = SentenceModel.MaxTokens - 2;
        var parts = new List<IReadOnlyList<int>>();
        var part = new List<int>(room);
        void Close()
        {
            if (part.Count == 0) return;
            parts.Add([tokenizer.StartId, .. part, tokenizer.EndId]);
            part.Clear();
        }
        foreach (var word in tokenizer.EncodeWords(text))
        {
            if (part.Count + word.Length > room) Close();
            foreach (var piece in word.Chunk(room))
            {
                if (part.Count + piece.Length > room) Close();
                part.AddRange(piece);
            }
            if (parts.Count >= MaxParts) break;
        }
        Close();
        return parts.Count > MaxParts ? parts.GetRange(0, MaxParts) : parts;
    }

    private async Task<IReadOnlyList<float[]>> RunAsync(Func<UnigramTokenizer, List<IReadOnlyList<int>>> encode, CancellationToken ct)
    {
        if (!IsAvailable || _broken) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (Load() is not var (tokenizer, model)) return (IReadOnlyList<float[]>)[];
                var texts = encode(tokenizer);
                var vectors = new List<float[]>(texts.Count);
                // One text per run: the int8 model scales each run's numbers together, so padding a shorter text to
                // a longer one beside it would change its vector.
                foreach (var one in texts)
                {
                    ct.ThrowIfCancellationRequested();
                    vectors.AddRange(model.Embed([one]));
                }
                return vectors;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _idle.Change(IdleRelease, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>The tokenizer and the model, loaded now if they are not; null when they cannot be (logged once).</summary>
    private (UnigramTokenizer, SentenceModel)? Load()
    {
        if (_loaded is { } loaded) return loaded;
        try
        {
            var started = Environment.TickCount64;
            var files = _installer.Files;
            var tokenizer = UnigramTokenizer.Load(files.TokenizerFile);
            var model = new SentenceModel(files.ModelFile, Threads);
            _loaded = (tokenizer, model);
            _logger.LogInformation("Sentence model loaded in {Ms} ms", Environment.TickCount64 - started);
            return _loaded;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OnnxRuntimeException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "The sentence model could not be loaded");
            _broken = true;
            return null;
        }
    }

    /// <summary>Lets go of the model (after a while without texts, or when it is removed).</summary>
    private void Release()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            _loaded?.Model.Dispose();
            _loaded = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        Release();
    }
}
