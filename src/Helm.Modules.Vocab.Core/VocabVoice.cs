using Helm.Core.Services;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.Vocab;

/// <summary>
/// Hearing a word: said by a neural voice (Novel Reader's word pronouncer: Sonia in British English, Xiaoxiao in
/// Mandarin, or an offline voice of the device), or heard from real people in YouTube videos on YouGlish. The pronouncer
/// is made on the first word said: building it builds every speech engine, which must not happen while Helm starts.
/// </summary>
public sealed class VocabVoice(Func<WordPronouncer?>? pronouncer, IProcessLauncher? launcher)
{
    private WordPronouncer? _pronouncer;

    /// <summary>Why a word could not be said (shown by the page).</summary>
    public event EventHandler<string>? Problem;

    public bool CanSay => pronouncer is not null;

    /// <summary>Says the word; a word said while another plays cuts it. Called on the UI thread.</summary>
    public async Task SayAsync(string text, VocabLanguage language)
    {
        if (pronouncer is null || text.Trim().Length == 0) return;
        if ((_pronouncer ??= pronouncer()) is not { } voice) return;
        if (await voice.SayAsync(text, language == VocabLanguage.Chinese ? "zh" : "en") is { } problem)
            Problem?.Invoke(this, problem);
    }

    /// <summary>YouGlish's page for the word: real speakers saying it (British English for English words).</summary>
    public static string YouGlishUrl(string text, VocabLanguage language) =>
        $"https://youglish.com/pronounce/{Uri.EscapeDataString(text.Trim())}/{(language == VocabLanguage.Chinese ? "chinese" : "english/uk")}";

    public void OpenYouGlish(string text, VocabLanguage language) => launcher?.OpenUrl(YouGlishUrl(text, language));
}
