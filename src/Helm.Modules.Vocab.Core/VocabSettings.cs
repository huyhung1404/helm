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

    /// <summary>Android: a word to guess on the lock screen (a silent notification) each time the screen turns on.</summary>
    public bool LockScreenWord { get; set; } = true;

    /// <summary>Show a button that opens YouGlish: the word said by real people in YouTube videos.</summary>
    public bool ShowYouGlish { get; set; } = true;

    /// <summary>Words (and roots) per page of the lists; see <see cref="VocabSettingsViewModel.PageSizes"/>.</summary>
    public int ItemsShown { get; set; } = VocabSettingsViewModel.DefaultPageSize;

    /// <summary>PC: now and then a word pops up in a corner to guess (see <see cref="PopupMinSeconds"/>).</summary>
    public bool PopupEnabled { get; set; } = true;

    /// <summary>PC: the pop-up comes back after a random time between these two (seconds).</summary>
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

    /// <summary>Android widget background (see <see cref="Helm.Core.Settings.WidgetLook"/>).</summary>
    public WidgetBackground WidgetBackground { get; set; } = WidgetBackground.Card;

    /// <summary>Android widget background opacity, 0-100 %.</summary>
    public int WidgetOpacity { get; set; } = 100;

    public WidgetText WidgetText { get; set; } = WidgetText.Automatic;

    public WidgetLook WidgetLook => new(WidgetBackground, WidgetOpacity, WidgetText);

    public void Migrate(int fromVersion)
    {
    }
}
