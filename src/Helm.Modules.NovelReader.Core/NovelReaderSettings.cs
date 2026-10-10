using Helm.Core.Settings;
using Helm.Modules.NovelReader.Conversion;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Device-local preferences (the novels, names and progress are synced through <see cref="Library.NovelStore"/>).
/// Voices differ per device, so reading aloud is set here too. Stored in settings/novel-reader.json.
/// </summary>
public sealed class NovelReaderSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public ConvertMode Mode { get; set; } = ConvertMode.VietPhrase;

    /// <summary>A phrase is not used when a name starts inside it (QuickTranslator's "prioritize names").</summary>
    public bool PrioritizeNames { get; set; } = true;

    /// <summary>Show the Chinese under each paragraph.</summary>
    public bool ShowChinese { get; set; }

    public double FontSize { get; set; } = 18;

    /// <summary>Line height as a multiple of the font size.</summary>
    public double LineSpacing { get; set; } = 1.6;

    /// <summary>The panel next to the text: names of this novel, or suggested names.</summary>
    public bool ShowNamesPanel { get; set; }

    // ---- Reading aloud ---------------------------------------------------------------------------------------------

    /// <summary>"engine:voice"; the online HoaiMy voice by default.</summary>
    public string? VoiceId { get; set; } = Speech.SpeechCatalog.DefaultVoiceId;

    /// <summary>When the online voice cannot be reached, read with a Vietnamese Windows voice if one is installed.</summary>
    public bool FallbackToOfflineVoice { get; set; } = true;

    /// <summary>0.5 to 3 (1 is the voice's normal speed).</summary>
    public double SpeechRate { get; set; } = 1;

    /// <summary>0 to 2 (1 is the voice's normal pitch).</summary>
    public double SpeechPitch { get; set; } = 1;

    /// <summary>0 to 1.</summary>
    public double SpeechVolume { get; set; } = 1;

    /// <summary>Silence between paragraphs, in seconds (0 to 2).</summary>
    public double ParagraphPause { get; set; } = 0.3;

    /// <summary>Offer HoaiMy through a hidden Microsoft Edge (fast, online; it cannot be downloaded).</summary>
    public bool UseEdgeVoice { get; set; } = true;

    /// <summary>The VieNeu-TTS folder (with its .venv) for the voices on this PC; null: not set up.</summary>
    public string? LocalVoiceFolder { get; set; }

    /// <summary>The port the local voice server listens on (127.0.0.1 only).</summary>
    public int LocalVoicePort { get; set; } = 8765;

    /// <summary>Stop the local voice server after 10 minutes without reading, to free the GPU memory.</summary>
    public bool StopLocalVoiceWhenIdle { get; set; } = true;

    /// <summary>At the end of a chapter, reading aloud goes on with the next one.</summary>
    public bool ContinueToNextChapter { get; set; } = true;

    // ---- Finding names ---------------------------------------------------------------------------------------------

    /// <summary>Opening a novel for the first time looks for its character names and adds the sure ones.</summary>
    public bool AutoScanNames { get; set; } = true;

    /// <summary>How names are found: from how words are used (free, offline), or by an AI on this PC through MCP.</summary>
    public NameScanMode NameScanMode { get; set; } = NameScanMode.Logic;

    // ---- Dictionaries ----------------------------------------------------------------------------------------------

    /// <summary>File name → address for "Download dictionaries"; editable in case a source moves.</summary>
    public Dictionary<string, string> DictionarySources { get; set; } = new(DefaultSources, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> DefaultSources { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ChinesePhienAmWords.txt"] = "https://raw.githubusercontent.com/ldhieu304/file-vietphrase/main/ChinesePhienAmWords.txt",
        ["VietPhrase.txt"] = "https://raw.githubusercontent.com/ldhieu304/file-vietphrase/main/VietPhrase.txt",
        ["Names.txt"] = "https://raw.githubusercontent.com/ldhieu304/file-vietphrase/main/Names.txt",
        ["LuatNhan.txt"] = "https://raw.githubusercontent.com/truyencuatui/VietPhrase/master/LuatNhan.txt",
        ["Pronouns.txt"] = "https://raw.githubusercontent.com/ThanhPhuoc91/VietPhrase/main/Pronouns.txt",
    };

    // ---- This device -----------------------------------------------------------------------------------------------

    /// <summary>The novel open on this device; null opens the one read most recently on any device.</summary>
    public string? OpenBookId { get; set; }
}

/// <summary>How the name scan decides what is a name.</summary>
public enum NameScanMode
{
    /// <summary>From how words are used in the novel: free and offline.</summary>
    Logic,
    /// <summary>Claude Code on this PC reads the candidates through Helm's MCP tools and adds the names (the user's own Claude plan, no API key).</summary>
    Ai,
}
