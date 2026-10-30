using Helm.Core.Sync;

namespace Helm.Modules.Vocab;

/// <summary>
/// Words, their parts and the quiz answers on top of Helm Sync. Every write is local and immediate (the sync engine
/// uploads it in the background). Callable from any thread; <see cref="Changed"/> may be raised on a background thread
/// after a sync.
/// </summary>
public sealed class VocabStore
{
    public const string WordsCollection = "vocab.words";
    public const string PartsCollection = "vocab.parts";
    public const string ReviewsCollection = "vocab.reviews";

    private readonly ISyncedCollection<VocabWord> _words;
    private readonly ISyncedCollection<VocabPart> _parts;
    private readonly ISyncedLog<VocabReview> _reviews;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public VocabStore(ISyncedCollection<VocabWord> words, ISyncedCollection<VocabPart> parts, ISyncedLog<VocabReview> reviews, TimeProvider? time = null)
    {
        _words = words;
        _parts = parts;
        _reviews = reviews;
        _time = time ?? TimeProvider.System;
        _words.Changed += OnChanged;
        _parts.Changed += OnChanged;
        _reviews.Changed += OnReviewsChanged;
    }

    /// <summary>Raised after any local write or synced change of words or parts.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a quiz answer is written here or arrives from another device.</summary>
    public event EventHandler? ReviewsChanged;

    public DateTimeOffset Now => _time.GetUtcNow();

    // ---- Words ---------------------------------------------------------------------------------------------------

    /// <summary>Every word, newest first.</summary>
    public IReadOnlyList<SyncedItem<VocabWord>> Words() =>
        _words.All().OrderByDescending(w => w.Value.CreatedAt).ThenByDescending(w => w.Id, StringComparer.Ordinal).ToList();

    /// <summary>Words waiting for an analysis, oldest first (the order they were added).</summary>
    public IReadOnlyList<SyncedItem<VocabWord>> Pending() =>
        _words.All().Where(w => w.Value.Status == VocabStatus.Pending)
            .OrderBy(w => w.Value.CreatedAt).ThenBy(w => w.Id, StringComparer.Ordinal).ToList();

    public VocabWord? GetWord(string id) => _words.Get(id);

    /// <summary>The word written this way (case ignored), if the user has it.</summary>
    public SyncedItem<VocabWord>? FindWord(string text)
    {
        var key = VocabText.Key(text);
        return _words.All().FirstOrDefault(w => VocabText.Key(w.Value.Text) == key);
    }

    /// <summary>Adds a word to analyze; one the user already has is returned as it is.</summary>
    /// <returns>The word's id, and whether it is new.</returns>
    public (string Id, bool Added) AddWord(string text, string note = "")
    {
        text = VocabText.Tidy(text);
        if (text.Length == 0) throw new ArgumentException("Type a word first.", nameof(text));
        if (VocabText.Length(text) > VocabText.MaxWordLength) throw new ArgumentException($"A word or phrase is at most {VocabText.MaxWordLength} characters.", nameof(text));
        lock (_gate)
        {
            if (FindWord(text) is { } existing) return (existing.Id, false);
            var id = _words.Add(new VocabWord
            {
                Language = VocabText.LanguageOf(text),
                Text = text,
                Note = note.Trim(),
                Status = VocabStatus.Pending,
                CreatedAt = Now,
            });
            return (id, true);
        }
    }

    /// <returns>False when the word no longer exists.</returns>
    public bool UpdateWord(string id, Func<VocabWord, VocabWord> change)
    {
        lock (_gate)
        {
            if (_words.Get(id) is not { } current) return false;
            var updated = change(current);
            if (updated != current) _words.Upsert(id, updated);
            return true;
        }
    }

    /// <summary>Sends the word back to the agents with what the user found wrong; the old analysis stays until a new one.</summary>
    public bool RequestRedo(string id, string note) =>
        UpdateWord(id, w => w with { Status = VocabStatus.Pending, RedoNote = note.Trim() });

    /// <summary>Deletes the word. Its parts stay: other words may share them.</summary>
    public bool DeleteWord(string id)
    {
        lock (_gate) return _words.Delete(id);
    }

    /// <summary>Writes an agent's analysis (see <see cref="VocabAnalyzer"/>; it checks everything before writing anything).</summary>
    /// <exception cref="VocabAnalysisException">The analysis is incomplete or does not fit the word.</exception>
    public VocabWord SaveAnalysis(string wordId, WordAnalysis analysis)
    {
        lock (_gate)
        {
            var word = _words.Get(wordId) ?? throw new VocabAnalysisException($"There is no word {wordId}.");
            var analyzer = new VocabAnalyzer(this, word);
            var links = analyzer.Write(analysis);
            var saved = word with
            {
                Status = VocabStatus.Analyzed,
                Phonetic = analysis.Phonetic.Trim(),
                Meaning = analysis.Meaning.Trim(),
                HanViet = analysis.HanViet?.Trim() ?? "",
                Traditional = analysis.Traditional?.Trim() is { } t && t != word.Text ? t : "",
                Explanation = analysis.Explanation?.Trim() ?? "",
                Parts = links,
                Examples = analysis.Examples?.Select(e => new VocabExample { Sentence = e.Sentence.Trim(), Translation = e.Translation.Trim() }).ToList() ?? word.Examples,
                Collocations = analysis.Collocations?.Select(c => c.Trim()).Where(c => c.Length > 0).ToList() ?? word.Collocations,
                Mnemonic = analysis.Mnemonic?.Trim() ?? word.Mnemonic,
                Confusables = analysis.Confusables?.Where(c => c.Word.Trim().Length > 0)
                    .Select(c => new VocabConfusable { Word = c.Word.Trim(), Note = c.Note.Trim() }).ToList() ?? word.Confusables,
                Level = analysis.Level is { } level ? VocabLevels.Normalize(level, word.Language) ?? "" : word.Level,
                PartOfSpeech = analysis.PartOfSpeech?.Trim() ?? word.PartOfSpeech,
                Topic = analysis.Topic?.Trim() ?? word.Topic,
                RedoNote = "",
                AnalyzedAt = Now,
            };
            _words.Upsert(wordId, saved);
            return saved;
        }
    }

    // ---- Parts ---------------------------------------------------------------------------------------------------

    public VocabPart? GetPart(string id) => _parts.Get(id);

    public IReadOnlyList<SyncedItem<VocabPart>> Parts() => _parts.All();

    /// <summary>Parts written this way in a language (an exact match; "re" also finds "re-").</summary>
    public IReadOnlyList<SyncedItem<VocabPart>> PartsWritten(string form, VocabLanguage language)
    {
        var key = FormKey(form);
        return _parts.All().Where(p => p.Value.Language == language && FormKey(p.Value.Form) == key).ToList();
    }

    internal static string FormKey(string form) => form.Trim().Trim('-').ToLowerInvariant();

    /// <summary>Changes a part's own fields (the user's wording, an agent's correction); its place in trees stays.</summary>
    /// <returns>False when the part no longer exists.</returns>
    public bool UpdatePart(string id, Func<VocabPart, VocabPart> change)
    {
        lock (_gate)
        {
            if (_parts.Get(id) is not { } current) return false;
            var updated = change(current);
            if (updated != current) _parts.Upsert(id, updated with { UpdatedAt = Now });
            return true;
        }
    }

    internal string AddPart(VocabPart part) => _parts.Add(part with { UpdatedAt = Now });

    internal void ReplacePart(string id, VocabPart part) => _parts.Upsert(id, part with { UpdatedAt = Now });

    /// <summary>Every word whose tree has this part somewhere, newest first.</summary>
    public IReadOnlyList<SyncedItem<VocabWord>> WordsUsing(string partId)
    {
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Has(string id, HashSet<string> path)
        {
            if (id == partId) return true;
            if (memo.TryGetValue(id, out var known)) return known;
            // A cycle can only come from two devices' edits meeting; it is cut rather than followed.
            if (!path.Add(id) || _parts.Get(id) is not { } part) return false;
            var found = part.Parts.Any(l => Has(l.PartId, path));
            path.Remove(id);
            return memo[id] = found;
        }
        return Words().Where(w => w.Value.Parts.Any(l => Has(l.PartId, new HashSet<string>(StringComparer.Ordinal)))).ToList();
    }

    // ---- Reviews -------------------------------------------------------------------------------------------------

    public void Record(string wordId, bool remembered) =>
        _reviews.Append(new VocabReview { WordId = wordId, At = Now, Remembered = remembered });

    public IReadOnlyList<VocabReview> Reviews() => _reviews.All().Select(r => r.Value).OrderBy(r => r.At).ToList();

    private void OnChanged(object? sender, SyncedChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnReviewsChanged(object? sender, SyncedChangedEventArgs e) => ReviewsChanged?.Invoke(this, EventArgs.Empty);
}
