using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader.Speech;

public enum ReadAloudState
{
    Stopped,
    /// <summary>Waiting for the voice to synthesize the sentence (or for the next chapter).</summary>
    Loading,
    Playing,
    Paused,
}

/// <summary>A place in the novel: chapter, paragraph (0 is the chapter title) and sentence in it.</summary>
public readonly record struct ReadingPosition(int Chapter, int Paragraph, int Sentence);

/// <summary>What reading aloud needs from the page: the open chapter's sentences and a way to open the next chapter.</summary>
public interface IReadAloudHost
{
    /// <summary>The open novel (downloaded sound is kept per novel).</summary>
    string? BookId { get; }

    int ChapterIndex { get; }

    int ChapterCount { get; }

    int ParagraphCount { get; }

    /// <summary>The sentences of a paragraph of the open chapter (none for a scene break).</summary>
    IReadOnlyList<Sentence> SentencesOf(int paragraph);

    /// <summary>The sentences of every paragraph of any chapter, converted off the UI thread (for downloading).</summary>
    Task<IReadOnlyList<IReadOnlyList<Sentence>>> ChapterSentencesAsync(int chapter, CancellationToken ct);

    /// <summary>Opens a chapter for reading on; false when it cannot be opened.</summary>
    Task<bool> OpenChapterForReadingAsync(int chapter);

    /// <summary>What the system media controls show: the novel and the chapter.</summary>
    (string Title, string Subtitle) NowPlaying { get; }
}

/// <summary>
/// Reading aloud like an audiobook player. It speaks a sentence at a time, with the voice's silence at both ends cut so
/// sentences follow each other without a pause. While it reads, the sentences ahead (on into the next chapters, see
/// <see cref="PrefetchChapters"/>) are synthesized and held in memory only, so after the first sentence nothing waits
/// for the network and going back plays at once. No sound is ever written to the disk: what was played is let go past
/// <see cref="PlayedBytes"/>, and downloading ahead waits while <see cref="AheadBytes"/> is waiting to be played.
/// Pause stops in the middle of a sentence; it skips by sentence or paragraph, goes on with the next chapter, stops on
/// a sleep timer (fading out), and falls back to an offline Vietnamese voice when the online one fails. Call it on the
/// UI thread; it raises its events there too.
/// </summary>
public sealed class ReadAloudController
{
    /// <summary>
    /// Sentences of the next chapter downloaded ahead when reading does not go on by itself, so opening it does not
    /// wait. When it does go on (<see cref="ContinueToNextChapter"/>), whole chapters are downloaded ahead
    /// (<see cref="PrefetchChapters"/>).
    /// </summary>
    public const int NextChapterLead = 5;

    /// <summary>The most chapters past the one being read that may be downloaded ahead.</summary>
    public const int MaxPrefetchChapters = 10;

    /// <summary>How long reading waits before asking an online voice that did not answer again (no offline voice to fall back on).</summary>
    public IReadOnlyList<TimeSpan> OnlineRetryWaits { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Sound already played, held for going back at once.</summary>
    public const long PlayedBytes = 16L * 1024 * 1024;

    /// <summary>Sound downloaded ahead and not played yet: downloading ahead waits while this much is waiting.</summary>
    public const long AheadBytes = 48L * 1024 * 1024;

    private readonly SpeechCatalog _catalog;
    private readonly IAudioOutput _output;
    private readonly TimeProvider _time;
    private readonly ILogger<ReadAloudController> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, HeldAudio> _memory = new(StringComparer.Ordinal);
    /// <summary>Played sentences, the one played longest ago first (the first to be let go).</summary>
    private readonly LinkedList<string> _played = new();
    private readonly Dictionary<string, Task<SpeechAudio>> _inFlight = new(StringComparer.Ordinal);
    private IDirectSpeechEngine? _direct;
    private volatile bool _sleepReached;
    /// <summary>Background requests per engine (<see cref="ISpeechEngine.Parallel"/>); the sentence to play now does not wait for them.</summary>
    private readonly Dictionary<string, SemaphoreSlim> _slots = new(StringComparer.Ordinal);
    private IReadAloudHost? _host;
    private CancellationTokenSource? _run;
    private PrefetchRun? _prefetch;
    private TaskCompletionSource? _resumed;
    private SpeechVoice? _fallback;
    private ReadAloudState _state;
    private int _readyThrough = -1;

    public ReadAloudController(SpeechCatalog catalog, IAudioOutput output, TimeProvider? time = null, ILogger<ReadAloudController>? logger = null)
    {
        _catalog = catalog;
        _output = output;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReadAloudController>.Instance;
    }

    // ---- Options, read before each sentence so changes apply from the next one --------------------------------------

    public Func<string?> VoiceId { get; set; } = () => SpeechCatalog.DefaultVoiceId;

    public Func<SpeechOptions> Options { get; set; } = () => new SpeechOptions(1, 1, 1);

    /// <summary>Seconds of silence between paragraphs (sentences follow each other with none: <see cref="SpeechSilence"/>).</summary>
    public Func<double> ParagraphPause { get; set; } = () => 0.15;

    public Func<bool> ContinueToNextChapter { get; set; } = () => true;

    /// <summary>
    /// How many chapters past the one being read are downloaded ahead while reading goes on by itself (1 to
    /// <see cref="MaxPrefetchChapters"/>); further ones wait until reading gets nearer, so nothing is downloaded that may
    /// never be heard.
    /// </summary>
    public Func<int> PrefetchChapters { get; set; } = () => 2;

    public Func<bool> FallbackToOfflineVoice { get; set; } = () => true;

    // ---- State -----------------------------------------------------------------------------------------------------

    public ReadAloudState State => _state;

    public bool IsActive => _state != ReadAloudState.Stopped;

    /// <summary>The sentence being read, or where reading will start.</summary>
    public ReadingPosition Position { get; private set; }

    public string CurrentText { get; private set; } = "";

    /// <summary>The last paragraph of the open chapter whose sound is ready from the one being read (-1: none).</summary>
    public int ReadyThrough => _readyThrough;

    /// <summary>When the sleep timer stops reading (null: no timer, or the end of the chapter).</summary>
    public DateTimeOffset? SleepAt { get; private set; }

    public bool SleepAtChapterEnd { get; private set; }

    /// <summary>The voice and settings sound is downloaded for now (null when no voice can speak).</summary>
    public string? Profile => CurrentVoice() is { } voice ? SpeechKeys.Profile(voice, Options()) : null;

    public event EventHandler? StateChanged;

    public event EventHandler<ReadingPosition>? PositionChanged;

    /// <summary>More of the open chapter is ready to play (see <see cref="ReadyThrough"/>); may come on any thread.</summary>
    public event EventHandler? ReadyChanged;

    /// <summary>Something the reader should know: a fallback voice, an error, the sleep timer stopped reading; empty: it is over.</summary>
    public event EventHandler<string>? Notice;

    public void Attach(IReadAloudHost host) => _host = host;

    // ---- Commands --------------------------------------------------------------------------------------------------

    /// <summary>Starts reading at a paragraph and sentence of the open chapter.</summary>
    public void Play(int paragraph, int sentence = 0)
    {
        if (_host is null) return;
        _fallback = null;
        StartAt(new ReadingPosition(_host.ChapterIndex, paragraph, sentence));
    }

    /// <summary>Plays from <see cref="Position"/>, pauses, or resumes.</summary>
    public void PlayPause()
    {
        switch (_state)
        {
            case ReadAloudState.Playing:
            case ReadAloudState.Loading:
                Pause();
                break;
            case ReadAloudState.Paused:
                Resume();
                break;
            default:
                Play(Position.Paragraph, Position.Sentence);
                break;
        }
    }

    public void Pause()
    {
        if (_state is not (ReadAloudState.Playing or ReadAloudState.Loading)) return;
        _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _output.Pause();
        _direct?.Pause();
        SetState(ReadAloudState.Paused);
    }

    public void Resume()
    {
        if (_state != ReadAloudState.Paused) return;
        _output.Resume();
        _direct?.Resume();
        SetState(ReadAloudState.Playing);
        _resumed?.TrySetResult();
        _resumed = null;
    }

    /// <summary>Stops reading (and downloading ahead); <see cref="Position"/> stays, so Play goes on from there.</summary>
    public void Stop()
    {
        _run?.Cancel();
        _run = null;
        StopDirect();
        StopPrefetch();
        _resumed?.TrySetResult();
        _resumed = null;
        _output.ClearNowPlaying();
        SetState(ReadAloudState.Stopped);
    }

    /// <summary>
    /// The open chapter changed under the reader (another chapter was picked): while reading, read on from there; while
    /// paused, stop and point there (the reader paused to read on by hand, and play starts the chapter they reached);
    /// otherwise just point there.
    /// </summary>
    public void ChapterChanged(int paragraph = 0)
    {
        if (_host is null) return;
        var position = new ReadingPosition(_host.ChapterIndex, paragraph, 0);
        SetReadyThrough(-1);
        if (_state == ReadAloudState.Paused) Stop();
        if (IsActive) StartAt(position);
        else SetPosition(position);
    }

    public void NextSentence() => Jump(NextOf(Position, sentenceStep: true));

    public void PreviousSentence() => Jump(PreviousOf(Position, sentenceStep: true));

    public void NextParagraph() => Jump(NextOf(Position, sentenceStep: false));

    /// <summary>Back to the start of this paragraph, or to the one before when already at its start.</summary>
    public void PreviousParagraph() => Jump(PreviousOf(Position, sentenceStep: false));

    /// <summary>Jumps to a paragraph of the open chapter (the seek bar).</summary>
    public void Seek(int paragraph)
    {
        if (_host is null) return;
        Jump(new ReadingPosition(_host.ChapterIndex, Math.Clamp(paragraph, 0, Math.Max(0, _host.ParagraphCount - 1)), 0));
    }

    /// <summary>Points at a place without reading (a reopened novel, a scrolled page).</summary>
    public void SetPosition(ReadingPosition position)
    {
        if (IsActive) return;
        Position = position;
        PositionChanged?.Invoke(this, position);
    }

    /// <summary>Stops reading after <paramref name="duration"/>; null turns the timer off.</summary>
    public void SetSleepTimer(TimeSpan? duration)
    {
        SleepAtChapterEnd = false;
        SleepAt = duration is { } d ? _time.GetUtcNow() + d : null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSleepAtChapterEnd()
    {
        SleepAt = null;
        SleepAtChapterEnd = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Downloads the first sentences from a place in the open chapter in the background, so pressing play there starts
    /// at once (used when a chapter is opened with an online voice).
    /// </summary>
    public void Warm(int paragraph, int sentence, int count = 3)
    {
        if (CurrentVoice() is { } directVoice && _catalog.EngineOf(directVoice) is IDirectSpeechEngine warming)
        {
            _ = warming.WarmUpAsync(CancellationToken.None);
            return;
        }
        if (_host?.BookId is null || CurrentVoice() is not { IsOnline: true } voice || IsActive) return;
        var options = Options();
        var texts = new List<string>();
        var position = new ReadingPosition(_host.ChapterIndex, paragraph, sentence);
        for (var i = 0; i < count && position.Paragraph < _host.ParagraphCount; i++)
        {
            var sentences = _host.SentencesOf(position.Paragraph);
            if (position.Sentence < sentences.Count) texts.Add(sentences[position.Sentence].Text);
            var next = NextOf(position, sentenceStep: true);
            if (next == position) break;
            position = next;
        }
        _ = Task.Run(async () =>
        {
            foreach (var text in texts)
            {
                try
                {
                    await EnsureAsync(voice, options, text, background: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SpeechUnavailableException or OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    // ---- Reading ---------------------------------------------------------------------------------------------------

    private void Jump(ReadingPosition position)
    {
        if (IsActive) StartAt(position);
        else SetPosition(position);
    }

    private void StartAt(ReadingPosition position)
    {
        _run?.Cancel();
        StopDirect();
        _resumed?.TrySetResult();
        _resumed = null;
        var run = new CancellationTokenSource();
        _run = run;
        StartPrefetch(position);
        _ = RunAsync(position, run);
    }

    private async Task RunAsync(ReadingPosition position, CancellationTokenSource run)
    {
        var ct = run.Token;
        var host = _host!;
        try
        {
            SetState(ReadAloudState.Loading);
            var lastParagraph = -1;
            while (!ct.IsCancellationRequested)
            {
                if (position.Paragraph >= host.ParagraphCount)
                {
                    if (SleepAtChapterEnd)
                    {
                        SleepAtChapterEnd = false;
                        Notice?.Invoke(this, "Sleep timer: stopped at the end of the chapter.");
                        Position = position;
                        break;
                    }
                    if (!ContinueToNextChapter() || host.ChapterIndex + 1 >= host.ChapterCount) break;
                    SetState(ReadAloudState.Loading);
                    if (!await host.OpenChapterForReadingAsync(host.ChapterIndex + 1) || ct.IsCancellationRequested) break;
                    position = new ReadingPosition(host.ChapterIndex, 0, 0);
                    SetReadyThrough(-1);
                    KeepPrefetching(position);
                    continue;
                }
                var sentences = host.SentencesOf(position.Paragraph);
                if (position.Sentence >= sentences.Count)
                {
                    position = new ReadingPosition(position.Chapter, position.Paragraph + 1, 0);
                    continue;
                }
                if (lastParagraph >= 0 && position.Paragraph != lastParagraph && ParagraphPause() > 0)
                    await Task.Delay(TimeSpan.FromSeconds(ParagraphPause()), _time, ct);
                lastParagraph = position.Paragraph;

                var sentence = sentences[position.Sentence];
                Position = position;
                CurrentText = sentence.Text;
                PositionChanged?.Invoke(this, position);
                var (title, subtitle) = host.NowPlaying;
                _output.ShowNowPlaying(title, subtitle);
                KeepPrefetching(position);
                UpdateReady(position);

                if (CurrentVoice() is { } speaking && _catalog.EngineOf(speaking) is IDirectSpeechEngine direct)
                {
                    var after = await SpeakChunkAsync(direct, speaking, position, ct);
                    if (after is not { } next) break;
                    if (_sleepReached)
                    {
                        _sleepReached = false;
                        SleepAt = null;
                        Notice?.Invoke(this, "Sleep timer: reading stopped.");
                        break;
                    }
                    position = next;
                    lastParagraph = -1;
                    continue;
                }

                if (_state != ReadAloudState.Paused) SetState(ReadAloudState.Loading);
                var audio = await AudioAsync(sentence.Text, ct);
                if (audio is null) break;
                await WhileResumedAsync(ct);
                if (_state != ReadAloudState.Paused) SetState(ReadAloudState.Playing);

                var last = SleepAt is { } at && _time.GetUtcNow() >= at - EstimateLength(audio);
                if (last)
                {
                    await PlayFadingOutAsync(audio, ct);
                    SleepAt = null;
                    Position = NextOf(position, sentenceStep: true);
                    Notice?.Invoke(this, "Sleep timer: reading stopped.");
                    break;
                }
                await _output.PlayAsync(audio, ct);
                position = new ReadingPosition(position.Chapter, position.Paragraph, position.Sentence + 1);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading aloud stopped");
            Notice?.Invoke(this, "Reading aloud stopped: " + ex.Message);
        }
        finally
        {
            if (_run == run)
            {
                _run = null;
                StopPrefetch();
                StopDirect();
                _output.ClearNowPlaying();
                SetState(ReadAloudState.Stopped);
                PositionChanged?.Invoke(this, Position);
            }
            run.Dispose();
        }
    }

    /// <summary>The sound of the sentence to play now: kept, being downloaded, or synthesized now; null when no voice can speak.</summary>
    private async Task<SpeechAudio?> AudioAsync(string text, CancellationToken ct)
    {
        var voice = CurrentVoice();
        if (voice is null)
        {
            Notice?.Invoke(this, "There is no voice to read with. Pick one in Novel Reader's settings.");
            return null;
        }
        try
        {
            return await EnsureAsync(voice, Options(), text, background: false, ct);
        }
        catch (SpeechUnavailableException ex) when (voice.IsOnline)
        {
            _logger.LogWarning(ex, "Online voice unavailable");
            if (FallbackToOfflineVoice() && _catalog.OfflineVietnamese is { } offline)
            {
                _fallback = offline;
                Notice?.Invoke(this, $"The online voice is not reachable, so {offline.Name} reads for now.");
                StopPrefetch();
                return await EnsureAsync(offline, Options(), text, background: false, ct);
            }
            // No offline voice: wait and try again (a weak connection, a busy service) before giving up, so listening
            // with the screen off does not just stop.
            foreach (var wait in OnlineRetryWaits)
            {
                Notice?.Invoke(this, $"{voice.Name} is not answering. Trying again in {wait.TotalSeconds:0} s…");
                await Task.Delay(wait, ct);
                try
                {
                    var audio = await EnsureAsync(voice, Options(), text, background: false, ct);
                    Notice?.Invoke(this, "");
                    return audio;
                }
                catch (SpeechUnavailableException again)
                {
                    _logger.LogWarning(again, "Online voice still unavailable");
                }
            }
            Notice?.Invoke(this, ex.Message + " Check the connection, or pick a Vietnamese voice that works offline.");
            return null;
        }
        catch (SpeechUnavailableException ex)
        {
            Notice?.Invoke(this, ex.Message);
            return null;
        }
    }

    private SpeechVoice? CurrentVoice() =>
        _fallback ?? _catalog.Find(VoiceId()) ?? _catalog.Find(SpeechCatalog.DefaultVoiceId) ?? _catalog.Voices.FirstOrDefault();

    /// <summary>
    /// The sound of one sentence: from memory, from a request already running, or synthesized now. Background requests
    /// share the engine's <see cref="ISpeechEngine.Parallel"/> slots; the sentence to play does not wait. The sentence
    /// to play (<paramref name="background"/> false) counts as played from here on.
    /// </summary>
    private async Task<SpeechAudio> EnsureAsync(SpeechVoice voice, SpeechOptions options, string text, bool background, CancellationToken ct)
    {
        var key = SpeechKeys.Key(SpeechKeys.Profile(voice, options), text);
        var audio = await HeldOrSynthesizedAsync(key, voice, options, text, background, ct).ConfigureAwait(false);
        if (!background) MarkPlayed(key);
        return audio;
    }

    private async Task<SpeechAudio> HeldOrSynthesizedAsync(string key, SpeechVoice voice, SpeechOptions options, string text, bool background,
        CancellationToken ct)
    {
        Task<SpeechAudio>? running;
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var held)) return held.Audio;
            _inFlight.TryGetValue(key, out running);
        }
        if (running is null)
        {
            if (_catalog.EngineOf(voice) is not { } engine) throw new SpeechUnavailableException($"The voice {voice.Name} is not available.");
            lock (_gate)
            {
                // Another request may have finished it since the first look.
                if (_memory.TryGetValue(key, out var done)) return done.Audio;
                if (!_inFlight.TryGetValue(key, out running))
                {
                    running = SynthesizeAsync(engine, voice, options, text, key, background);
                    // Whoever waited may have gone (a jump, a stop): the failure is still seen, not left unobserved.
                    _ = running.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    // An engine that fails at once has already removed its entry: keeping the failure would answer
                    // every later request for this sentence with it.
                    if (!running.IsCompleted) _inFlight[key] = running;
                }
            }
        }
        return await running.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<SpeechAudio> SynthesizeAsync(ISpeechEngine engine, SpeechVoice voice, SpeechOptions options, string text, string key,
        bool background)
    {
        var slots = background ? SlotsOf(engine) : null;
        try
        {
            if (slots is not null) await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                // Without the voice's padding at both ends, one sentence follows the last without a pause.
                var audio = SpeechSilence.Trim(await engine.SynthesizeAsync(text, voice, options, CancellationToken.None).ConfigureAwait(false));
                Remember(key, audio);
                return audio;
            }
            finally
            {
                slots?.Release();
            }
        }
        finally
        {
            lock (_gate) _inFlight.Remove(key);
        }
    }

    private SemaphoreSlim SlotsOf(ISpeechEngine engine)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(engine.Prefix, out var slots))
                _slots[engine.Prefix] = slots = new SemaphoreSlim(WorkersFor(engine));
            return slots;
        }
    }

    private static int WorkersFor(ISpeechEngine engine) => Math.Clamp(engine.Parallel, 1, 16);

    // ---- Sound in memory -------------------------------------------------------------------------------------------

    /// <summary>A sentence's sound, waiting to be played (for the download run that holds it) or played already.</summary>
    private sealed class HeldAudio(SpeechAudio audio, PrefetchRun? run)
    {
        public SpeechAudio Audio { get; } = audio;

        public PrefetchRun? Run { get; set; } = run;

        public bool Played { get; set; }
    }

    /// <summary>Holds a new sentence as waiting to be played, for the download run going on now.</summary>
    private void Remember(string key, SpeechAudio audio)
    {
        lock (_gate)
        {
            if (_memory.ContainsKey(key)) return;
            _memory[key] = new HeldAudio(audio, _prefetch);
            Trim();
        }
    }

    /// <summary>The sentence is being played: it may be let go once newer ones were played.</summary>
    private void MarkPlayed(string key)
    {
        lock (_gate)
        {
            if (!_memory.TryGetValue(key, out var held)) return;
            if (held.Played) _played.Remove(key);
            held.Played = true;
            _played.AddLast(key);
            Trim();
        }
    }

    /// <summary>
    /// Whether a sentence is in memory; one waiting to be played becomes the run's (a new run over sound already
    /// downloaded, after a jump). One played already stays among the played (going back plays it while it is held).
    /// </summary>
    private bool Claim(string key, PrefetchRun run)
    {
        lock (_gate)
        {
            if (!_memory.TryGetValue(key, out var held)) return false;
            if (!held.Played) held.Run = run;
            return true;
        }
    }

    /// <summary>Bytes the run downloaded that were not played yet.</summary>
    private long WaitingBytes(PrefetchRun run)
    {
        lock (_gate) return _memory.Values.Where(h => !h.Played && h.Run == run).Sum(h => (long)h.Audio.Data.Length);
    }

    /// <summary>
    /// Lets go of what was played longest ago past <see cref="PlayedBytes"/>, then of sound no run waits for any more
    /// (left by a jump or a stop) past <see cref="AheadBytes"/>. Sound the current run waits to play stays. Under the lock.
    /// </summary>
    private void Trim()
    {
        var played = _memory.Values.Where(h => h.Played).Sum(h => (long)h.Audio.Data.Length);
        while (played > PlayedBytes && _played.First is { } oldest)
        {
            played -= _memory[oldest.Value].Audio.Data.Length;
            _memory.Remove(oldest.Value);
            _played.RemoveFirst();
        }
        var waiting = _memory.Values.Where(h => !h.Played).Sum(h => (long)h.Audio.Data.Length);
        if (waiting <= AheadBytes) return;
        foreach (var (key, held) in _memory.Where(e => !e.Value.Played && e.Value.Run != _prefetch).ToList())
        {
            _memory.Remove(key);
            waiting -= held.Audio.Data.Length;
            if (waiting <= AheadBytes) return;
        }
    }

    // ---- Voices that play by themselves (HoaiMy through Edge) ----------------------------------------------------

    /// <summary>Characters handed to a voice that plays by itself at once: about 45 s of speech, one short pause each.</summary>
    public const int DirectChunk = 700;

    /// <summary>
    /// Speaks whole sentences from <paramref name="start"/> (across paragraphs, up to <see cref="DirectChunk"/>
    /// characters, never past the chapter) as one text, following the words the voice reports to move the highlight and
    /// the saved place sentence by sentence. Returns where to go on, or null when reading must stop.
    /// </summary>
    private async Task<ReadingPosition?> SpeakChunkAsync(IDirectSpeechEngine direct, SpeechVoice voice, ReadingPosition start, CancellationToken ct)
    {
        var host = _host!;
        var parts = new List<(ReadingPosition Position, int Offset, string Text)>();
        var text = new System.Text.StringBuilder();
        var position = start;
        while (position.Paragraph < host.ParagraphCount)
        {
            var sentences = host.SentencesOf(position.Paragraph);
            if (position.Sentence < sentences.Count)
            {
                var sentence = sentences[position.Sentence].Text;
                if (parts.Count > 0 && text.Length + sentence.Length > DirectChunk) break;
                if (text.Length > 0) text.Append(position.Sentence == 0 ? "\n" : " ");
                parts.Add((position, text.Length, sentence));
                text.Append(sentence);
            }
            var next = NextOf(position, sentenceStep: true);
            if (next == position) break;
            position = next;
        }
        if (parts.Count == 0) return position;

        _direct = direct;
        await WhileResumedAsync(ct);
        if (_state != ReadAloudState.Paused) SetState(ReadAloudState.Loading);
        var current = 0;
        void Follow(int at)
        {
            var index = current;
            while (index + 1 < parts.Count && parts[index + 1].Offset <= at) index++;
            if (index == current) return;
            current = index;
            Position = parts[index].Position;
            CurrentText = parts[index].Text;
            PositionChanged?.Invoke(this, Position);
            if (SleepAt is { } due && _time.GetUtcNow() >= due)
            {
                // The sleep timer stops at a sentence, not in the middle of one.
                _sleepReached = true;
                Position = parts[index].Position;
                direct.Cancel();
            }
        }
        try
        {
            var speaking = direct.SpeakAsync(text.ToString(), voice, Options(), ct, Follow);
            if (_state != ReadAloudState.Paused) SetState(ReadAloudState.Playing);
            await speaking;
            return position;
        }
        catch (OperationCanceledException) when (_sleepReached && !ct.IsCancellationRequested)
        {
            return Position;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not the reader stopping: the voice gave up. Say so instead of stopping silently.
            StopDirect();
            Notice?.Invoke(this, $"{voice.Name} stopped answering. Press play to try again, or pick another voice.");
            return null;
        }
        catch (SpeechUnavailableException ex)
        {
            StopDirect();
            _logger.LogWarning(ex, "Voice through Edge unavailable");
            if (FallbackToOfflineVoice() && _catalog.OfflineVietnamese is { } offline)
            {
                _fallback = offline;
                Notice?.Invoke(this, $"{voice.Name} is not reachable, so {offline.Name} reads for now.");
                // Go on from the sentence being read, with the offline voice.
                return Position;
            }
            Notice?.Invoke(this, ex.Message);
            return null;
        }
    }

    private void StopDirect()
    {
        _direct?.Cancel();
        _direct = null;
    }

    // ---- Downloading ahead -----------------------------------------------------------------------------------------

    /// <summary>
    /// One run of downloading ahead: a single queue from where reading started, through the rest of that chapter and on
    /// into the next ones with no stop between them, as many at a time as the engine does well. While reading goes on by
    /// itself it stays <see cref="PrefetchChapters"/> chapters ahead of the chapter being read and waits there until
    /// reading moves on (<see cref="MoveTo"/>); otherwise it ends with the start of the next chapter.
    /// </summary>
    private sealed class PrefetchRun(string profile, int startChapter, int reading, int ahead, bool continuing)
    {
        private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reading = reading;
        private int _ahead = ahead;

        public CancellationTokenSource Cancel { get; } = new();

        public string Profile { get; } = profile;

        public int StartChapter { get; } = startChapter;

        public bool Continuing { get; } = continuing;

        public Task Running { get; set; } = Task.CompletedTask;

        public int Reading => Volatile.Read(ref _reading);

        /// <summary>The last chapter it may download now.</summary>
        public int Limit => Volatile.Read(ref _reading) + Volatile.Read(ref _ahead);

        /// <summary>Reading moved to <paramref name="chapter"/>: the window moves with it.</summary>
        public void MoveTo(int chapter, int ahead)
        {
            Volatile.Write(ref _reading, chapter);
            Volatile.Write(ref _ahead, ahead);
            Interlocked.Exchange(ref _moved, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        /// <summary>Waits until <paramref name="chapter"/> is inside the window.</summary>
        public async Task WaitForAsync(int chapter, CancellationToken ct)
        {
            while (true)
            {
                var moved = Volatile.Read(ref _moved);
                if (chapter <= Limit) return;
                await moved.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The sentences of one chapter in the queue.</summary>
    private sealed class PrefetchBatch(int chapter, IReadOnlyList<string> texts)
    {
        public int Chapter { get; } = chapter;

        public IReadOnlyList<string> Texts { get; } = texts;
    }

    /// <summary>
    /// Keeps downloading ahead for <paramref name="position"/>: the run going on follows reading into the next chapter;
    /// a new run starts after a new voice or speed (another recording), or when the run ended or cannot cover it.
    /// </summary>
    private void KeepPrefetching(ReadingPosition position)
    {
        if (_prefetch is { } run && run.Profile == Profile && run.Continuing == ContinueToNextChapter() && position.Chapter >= run.StartChapter
            && (position.Chapter == run.Reading || !run.Running.IsCompleted))
        {
            if (position.Chapter != run.Reading) run.MoveTo(position.Chapter, AheadChapters());
            return;
        }
        StartPrefetch(position);
    }

    private int AheadChapters() => Math.Clamp(PrefetchChapters(), 1, MaxPrefetchChapters);

    /// <summary>
    /// Downloads every sentence from <paramref name="from"/> to the end of the open chapter, then on through the next
    /// chapters (<see cref="PrefetchRun"/>), skipping what is kept already. Restarted after a jump, another chapter
    /// picked, or a new voice or speed.
    /// </summary>
    private void StartPrefetch(ReadingPosition from)
    {
        StopPrefetch();
        if (_host is not { BookId: not null } host || CurrentVoice() is not { } voice || _catalog.EngineOf(voice) is not { } engine
            || engine is IDirectSpeechEngine) return;
        var options = Options();
        var profile = SpeechKeys.Profile(voice, options);
        var current = new List<string>();
        for (var p = Math.Max(0, from.Paragraph); p < host.ParagraphCount; p++)
        {
            var sentences = host.SentencesOf(p);
            for (var s = p == from.Paragraph ? from.Sentence : 0; s < sentences.Count; s++) current.Add(sentences[s].Text);
        }
        var run = new PrefetchRun(profile, from.Chapter, from.Chapter, AheadChapters(), ContinueToNextChapter());
        _prefetch = run;
        var chapterCount = host.ChapterCount;
        var first = new PrefetchBatch(from.Chapter, current);
        run.Running = Task.Run(() => PrefetchAsync(run, host, voice, options, WorkersFor(engine), first, chapterCount));
    }

    private async Task PrefetchAsync(PrefetchRun run, IReadAloudHost host, SpeechVoice voice, SpeechOptions options, int workers,
        PrefetchBatch first, int chapterCount)
    {
        var ct = run.Cancel.Token;
        var take = new SemaphoreSlim(1, 1);
        var batch = first;
        var index = 0;
        var failures = 0;

        // The next sentence of the queue, moving on to the next chapter when one is done (and waiting while that one is
        // too far ahead); null when there is nothing more to download.
        async Task<(PrefetchBatch Batch, string Text)?> TakeAsync()
        {
            await take.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (index >= batch.Texts.Count)
                {
                    var chapter = batch.Chapter + 1;
                    if (chapter >= chapterCount) return null;
                    if (!run.Continuing && chapter > first.Chapter + 1) return null;
                    if (run.Continuing) await run.WaitForAsync(chapter, ct).ConfigureAwait(false);
                    var paragraphs = await host.ChapterSentencesAsync(chapter, ct).ConfigureAwait(false);
                    var texts = paragraphs.SelectMany(p => p.Select(s => s.Text)).ToList();
                    // Reading stops at the chapter's end: only the start of the next, for opening it by hand.
                    batch = new PrefetchBatch(chapter, run.Continuing ? texts : texts.Take(NextChapterLead).ToList());
                    index = 0;
                }
                return (batch, batch.Texts[index++]);
            }
            finally
            {
                take.Release();
            }
        }

        async Task Worker()
        {
            while (await TakeAsync().ConfigureAwait(false) is { } item)
            {
                var key = SpeechKeys.Key(run.Profile, item.Text);
                if (!Claim(key, run))
                {
                    // Far enough ahead is held: wait for reading to catch up (memory is all the sound has).
                    while (WaitingBytes(run) >= AheadBytes) await Task.Delay(TimeSpan.FromSeconds(1), _time, ct).ConfigureAwait(false);
                    try
                    {
                        await EnsureAsync(voice, options, item.Text, background: true, ct).ConfigureAwait(false);
                        Claim(key, run);
                        Volatile.Write(ref failures, 0);
                        if (item.Batch.Chapter == run.Reading) ReadyChanged?.Invoke(this, EventArgs.Empty);
                    }
                    catch (SpeechUnavailableException)
                    {
                        // Offline: stop downloading ahead; playing will fall back or report it.
                        if (Interlocked.Increment(ref failures) >= 3) throw new OperationCanceledException();
                    }
                }
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker())).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SpeechUnavailableException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downloading ahead stopped");
        }
        finally
        {
            // The other workers stop too (one gave up, or the run was replaced).
            try
            {
                run.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void StopPrefetch()
    {
        _prefetch?.Cancel.Cancel();
        _prefetch = null;
    }

    /// <summary>Whether every sentence of <paramref name="texts"/> is in memory for <paramref name="profile"/>.</summary>
    private bool AllHeld(string profile, IEnumerable<string> texts)
    {
        lock (_gate) return texts.All(text => _memory.ContainsKey(SpeechKeys.Key(profile, text)));
    }

    /// <summary>How far, from the sentence being read, the sound of the open chapter is ready without a gap.</summary>
    private void UpdateReady(ReadingPosition from)
    {
        if (_host is not { BookId: not null } host || Profile is not { } profile) return;
        var ready = from.Paragraph - 1;
        for (var p = from.Paragraph; p < host.ParagraphCount; p++)
        {
            var sentences = host.SentencesOf(p);
            var start = p == from.Paragraph ? from.Sentence : 0;
            if (!AllHeld(profile, sentences.Skip(start).Select(s => s.Text))) break;
            ready = p;
        }
        SetReadyThrough(ready);
    }

    /// <summary>Called when more sound arrived: works out how far the open chapter is ready (on the UI thread).</summary>
    public void RefreshReady()
    {
        if (IsActive || _readyThrough >= 0) UpdateReady(Position);
    }

    private void SetReadyThrough(int paragraph) => _readyThrough = paragraph;

    // ---- Small helpers ---------------------------------------------------------------------------------------------

    private async Task WhileResumedAsync(CancellationToken ct)
    {
        while (_state == ReadAloudState.Paused && _resumed is { } resumed)
            await resumed.Task.WaitAsync(ct);
    }

    /// <summary>The last sentence before the sleep timer: the volume goes down to nothing while it plays.</summary>
    private async Task PlayFadingOutAsync(SpeechAudio audio, CancellationToken ct)
    {
        var length = EstimateLength(audio);
        var playing = _output.PlayAsync(audio, ct);
        const int steps = 20;
        try
        {
            for (var i = 1; i <= steps && !playing.IsCompleted; i++)
            {
                await Task.WhenAny(playing, Task.Delay(length / steps, _time, ct));
                _output.Volume = Math.Max(0, 1 - (double)i / steps);
            }
            await playing;
        }
        finally
        {
            _output.Volume = 1;
        }
    }

    /// <summary>How long the sound lasts: MP3 from the online voice is 48 kbit/s; a WAV says its byte rate in its header.</summary>
    internal static TimeSpan EstimateLength(SpeechAudio audio)
    {
        if (audio.ContentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromSeconds(audio.Data.Length / 6000.0);
        var data = audio.Data;
        var byteRate = data.Length >= 44 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            ? BitConverter.ToInt32(data, 28)
            : 0;
        return TimeSpan.FromSeconds(Math.Max(0, data.Length - 44) / (double)(byteRate > 0 ? byteRate : 32000));
    }

    private ReadingPosition NextOf(ReadingPosition at, bool sentenceStep)
    {
        if (_host is null) return at;
        if (sentenceStep && at.Sentence + 1 < _host.SentencesOf(at.Paragraph).Count) return at with { Sentence = at.Sentence + 1 };
        for (var p = at.Paragraph + 1; p < _host.ParagraphCount; p++)
            if (_host.SentencesOf(p).Count > 0) return new ReadingPosition(at.Chapter, p, 0);
        // Past the last paragraph: reading goes on with the next chapter.
        return new ReadingPosition(at.Chapter, _host.ParagraphCount, 0);
    }

    private ReadingPosition PreviousOf(ReadingPosition at, bool sentenceStep)
    {
        if (_host is null) return at;
        if (at.Sentence > 0) return at with { Sentence = sentenceStep ? at.Sentence - 1 : 0 };
        for (var p = Math.Min(at.Paragraph, _host.ParagraphCount) - 1; p >= 0; p--)
        {
            var count = _host.SentencesOf(p).Count;
            if (count > 0) return new ReadingPosition(at.Chapter, p, sentenceStep ? count - 1 : 0);
        }
        return at with { Sentence = 0 };
    }

    private void SetState(ReadAloudState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
