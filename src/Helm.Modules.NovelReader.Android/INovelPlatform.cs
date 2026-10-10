using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.NovelReader;

/// <summary>What Novel Reader's pages need from Android (pickers, the phone's settings), behind a seam so the pages do not call Android directly.</summary>
public interface INovelPlatform
{
    /// <summary>Asks for a novel (.txt) and copies it where Novel Reader can read it; null when cancelled.</summary>
    Task<string?> PickNovelAsync(CancellationToken ct);

    /// <summary>Asks for QuickTranslator dictionary files and copies them, keeping their names; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickDictionariesAsync(CancellationToken ct);

    /// <summary>Asks for a picture and makes a small JPEG cover of it (at most 600 px); null when cancelled.</summary>
    Task<byte[]?> PickCoverAsync(CancellationToken ct);

    /// <summary>Starts the phone's own voices (Android text-to-speech) when Novel Reader is opened.</summary>
    void StartPhoneVoices();

    /// <summary>Reads a sentence at once with one of the phone's voices, to choose one.</summary>
    void PreviewPhoneVoice(SpeechVoice voice, string text);

    /// <summary>Android's text-to-speech settings (preferred engine, its voices).</summary>
    void OpenVoiceSettings();

    /// <summary>The engine's own screen for downloading voice data (e.g. Vietnamese for Google's engine).</summary>
    void InstallVoiceData();

    /// <summary>The phone's battery optimization list, where Helm can be allowed to run in the background.</summary>
    void OpenBatterySettings();

    /// <summary>Android does not limit Helm in the background (battery optimization is off for it).</summary>
    bool RunsFreelyInBackground { get; }
}
