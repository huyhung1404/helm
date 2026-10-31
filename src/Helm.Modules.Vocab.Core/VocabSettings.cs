using Helm.Core.Settings;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab;

/// <summary>
/// Device-local preferences (the words themselves are synced through <see cref="VocabStore"/>). Stored in
/// settings/vocab.json on both apps.
/// </summary>
public sealed class VocabSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Show a Chinese word's traditional form next to it when it has one.</summary>
    public bool ShowTraditional { get; set; } = true;

    /// <summary>How the Learn tab's cards ask: the word, its meaning, its sound, a sentence with a gap, or a mix.</summary>
    public VocabCardMode CardMode { get; set; } = VocabCardMode.WordToMeaning;

    /// <summary>Say the word when its meaning is shown.</summary>
    public bool AutoSay { get; set; }

    /// <summary>Chinese words' cards ask for the pinyin before the answer (tones optional), to practise the reading.</summary>
    public bool TypePinyin { get; set; } = true;

    /// <summary>PC: new words are broken down by Claude Code by itself, a moment after they arrive (from any device).</summary>
    public bool AutoAnalyze { get; set; } = true;

    /// <summary>The Words tab's order: 0 A–Z, 1 newest, 2 weakest first, 3 due soonest.</summary>
    public int WordsSort { get; set; }

    /// <summary>Show a button that opens YouGlish: the word said by real people in YouTube videos.</summary>
    public bool ShowYouGlish { get; set; } = true;

    /// <summary>Words (and roots) per page of the lists; see <see cref="VocabSettingsViewModel.PageSizes"/>.</summary>
    public int ItemsShown { get; set; } = VocabSettingsViewModel.DefaultPageSize;

    /// <summary>
    /// Now and then a word pops up to guess (see <see cref="PopupMinSeconds"/>): in a corner on the PC, over the other
    /// apps on the phone (once Helm may draw over them).
    /// </summary>
    public bool PopupEnabled { get; set; } = true;

    /// <summary>The pop-up comes back after a random time between these two (seconds).</summary>
    public int PopupMinSeconds { get; set; } = 20 * 60;

    public int PopupMaxSeconds { get; set; } = 60 * 60;

    /// <summary>The user typed the range (Custom) rather than picking one of the choices.</summary>
    public bool PopupCustom { get; set; }

    /// <summary>
    /// PC: where the user dragged the pop-up, as its bottom-left corner in screen units (it grows upwards when the meaning
    /// shows); null keeps it in the bottom-right corner.
    /// </summary>
    public double? PopupLeft { get; set; }

    public double? PopupBottom { get; set; }

    /// <summary>Android: a bubble that floats over the other apps; tapping it shows a word to guess.</summary>
    public bool BubbleEnabled { get; set; }

    /// <summary>
    /// Android: where the user left the bubble, as fractions of the screen's width and height (it sticks to the nearer
    /// side); null puts it at the right edge, a third of the way down.
    /// </summary>
    public double? BubbleX { get; set; }

    public double? BubbleY { get; set; }

    /// <summary>Android: apps (package names) over which neither the pop-up nor the bubble ever shows.</summary>
    public List<string> BlockedApps { get; set; } = [];

    public void Migrate(int fromVersion)
    {
    }
}
