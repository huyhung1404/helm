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

    /// <summary>Android: a word to guess on the lock screen (a silent notification) each time the screen turns on.</summary>
    public bool LockScreenWord { get; set; } = true;

    /// <summary>Show a button that opens YouGlish: the word said by real people in YouTube videos.</summary>
    public bool ShowYouGlish { get; set; } = true;

    /// <summary>Words listed at first; "Show more" adds as many again (0: all).</summary>
    public int ItemsShown { get; set; } = ListPager.DefaultSize;

    /// <summary>PC: now and then a word pops up in a corner to guess (see <see cref="PopupMinSeconds"/>).</summary>
    public bool PopupEnabled { get; set; } = true;

    /// <summary>PC: the pop-up comes back after a random time between these two (seconds).</summary>
    public int PopupMinSeconds { get; set; } = 20 * 60;

    public int PopupMaxSeconds { get; set; } = 60 * 60;

    /// <summary>The user typed the range (Custom) rather than picking one of the choices.</summary>
    public bool PopupCustom { get; set; }

    public void Migrate(int fromVersion)
    {
    }
}
